import {spawn} from 'node:child_process';

function uniqueNames(value) {
 if(!Array.isArray(value)||value.length>256||value.some(name=>typeof name!=='string'||!name.length||name.length>256))throw Error('Invalid inherited MCP server catalog');
 const names=[...new Set(value)];
 if(names.length!==value.length)throw Error('Duplicate inherited MCP server name');
 return names.sort();
}

export function discoverMcpServerNames(executable,cwd,env=process.env) {
 return new Promise((resolve,reject)=>{
  const child=spawn(executable,['mcp','list','--json','--disable','apps','--disable','plugins'],{cwd,env,stdio:['ignore','pipe','pipe'],windowsHide:true});
  let stdout='',stderr='',settled=false;
  const finish=(callback,value)=>{if(settled)return;settled=true;clearTimeout(timer);callback(value);};
  const fail=error=>{child.kill();finish(reject,error);};
  const timer=setTimeout(()=>fail(Error('MCP configuration discovery timed out')),30000);
  child.stdout.on('data',data=>{stdout+=data;if(stdout.length>1048576)fail(Error('MCP configuration discovery exceeded its bound'));});
  child.stderr.on('data',data=>{stderr+=data;if(stderr.length>1048576)fail(Error('MCP configuration diagnostics exceeded their bound'));});
  child.on('error',fail);
  child.on('exit',code=>{
   if(settled)return;
   if(code!==0){finish(reject,Error('MCP configuration discovery failed: '+stderr.trim()));return;}
   try {
    const rows=JSON.parse(stdout);
    if(!Array.isArray(rows)||rows.some(row=>!row||typeof row!=='object'))throw Error('Invalid MCP configuration response');
    finish(resolve,uniqueNames(rows.map(row=>row.name)));
   }catch(error){finish(reject,error);}
  });
 });
}

export function isolatedAppServerArgs(mcpServerNames,extra=[]) {
 const names=uniqueNames(mcpServerNames);
 const disabled=names.length?['-c','mcp_servers={'+names.map(name=>JSON.stringify(name)+'={enabled=false}').join(',')+'}']:[];
 return ['app-server','--stdio','--disable','hooks','--disable','apps','--disable','plugins',...disabled,...extra];
}

export async function verifyExternalToolConfiguration(request,mcpServerNames) {
 const expected=uniqueNames(mcpServerNames);
 const effective=(await request('config/read',{includeLayers:false})).config??{};
 const configured=effective.mcp_servers&&typeof effective.mcp_servers==='object'?effective.mcp_servers:{};
 const configuredNames=Object.keys(configured).sort();
 const enabledMcpServers=configuredNames.filter(name=>configured[name]?.enabled!==false);
 const result={
  appsFeatureEnabled:effective.features?.apps,
  pluginsFeatureEnabled:effective.features?.plugins,
  configuredMcpServers:configuredNames,
  enabledMcpServers,
 };
 if(result.appsFeatureEnabled!==false||result.pluginsFeatureEnabled!==false||JSON.stringify(configuredNames)!==JSON.stringify(expected)||enabledMcpServers.length)throw Error('External app-server configuration is not isolated: '+JSON.stringify(result));
 return result;
}

export async function verifyExternalToolIsolation(request,threadId,configuration) {
 const installed=await request('app/installed',{threadId,forceRefresh:false});
 const servers=await request('mcpServerStatus/list',{cursor:null,limit:100,detail:'toolsAndAuthOnly'});
 const apps=Array.isArray(installed.apps)?installed.apps:[];
 const mcpServers=Array.isArray(servers.data)?servers.data:[];
 const activeMcpServers=mcpServers.filter(server=>server.runtimeStatus!==null||server.serverInfo!==null||server.toolsError!==null||Object.keys(server.tools??{}).length||(server.resources??[]).length||(server.resourceTemplates??[]).length);
 const result={
  ...configuration,
  exposedApps:apps.map(app=>app.id??app.name??'unknown'),
  activeMcpServers:activeMcpServers.map(server=>server.name??server.id??'unknown'),
 };
 if(result.exposedApps.length||result.activeMcpServers.length)throw Error('External app-server tools are not isolated: '+JSON.stringify(result));
 return result;
}
