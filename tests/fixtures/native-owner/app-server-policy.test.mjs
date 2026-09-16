import test from 'node:test';
import assert from 'node:assert/strict';
import {isolatedAppServerArgs,verifyExternalToolConfiguration,verifyExternalToolIsolation} from '../../../bin/native-owner/app-server-policy.mjs';

function requestBoundary(responses) {
 const calls=[];
 const request=async(method,params)=>{
  calls.push({method,params});
  if(!(method in responses))throw Error('Unexpected request '+method);
  return responses[method];
 };
 return {request,calls};
}

test('isolated app-server arguments disable every inherited external capability',()=>{
 assert.deepEqual(isolatedAppServerArgs(['zeta','alpha'],['-c','windows.sandbox=unelevated']),[
  'app-server','--stdio','--disable','hooks','--disable','apps','--disable','plugins',
  '-c','mcp_servers={"alpha"={enabled=false},"zeta"={enabled=false}}',
  '-c','windows.sandbox=unelevated',
 ]);
});

test('the fake request boundary observes a fully isolated effective catalog',async()=>{
 const boundary=requestBoundary({
  'config/read':{config:{features:{apps:false,plugins:false},mcp_servers:{alpha:{enabled:false}}}},
  'app/installed':{apps:[]},
  'mcpServerStatus/list':{data:[]},
 });
 const configuration=await verifyExternalToolConfiguration(boundary.request,['alpha']);
 const result=await verifyExternalToolIsolation(boundary.request,'thread-1',configuration);
 assert.deepEqual(result,{
  appsFeatureEnabled:false,pluginsFeatureEnabled:false,configuredMcpServers:['alpha'],enabledMcpServers:[],exposedApps:[],activeMcpServers:[],
 });
 assert.deepEqual(boundary.calls,[
  {method:'config/read',params:{includeLayers:false}},
  {method:'app/installed',params:{threadId:'thread-1',forceRefresh:false}},
  {method:'mcpServerStatus/list',params:{cursor:null,limit:100,detail:'toolsAndAuthOnly'}},
 ]);
});

test('enabled app features and MCP servers are rejected at the effective configuration boundary',async()=>{
 for(const config of [
 {features:{apps:true,plugins:false},mcp_servers:{alpha:{enabled:false}}},
  {features:{apps:false,plugins:true},mcp_servers:{alpha:{enabled:false}}},
  {features:{apps:false,plugins:false},mcp_servers:{alpha:{enabled:true}}},
 ]) {
  const {request}=requestBoundary({'config/read':{config}});
  await assert.rejects(verifyExternalToolConfiguration(request,['alpha']),/External app-server configuration is not isolated/);
 }
});

test('exposed apps and active MCP tools are rejected at the thread boundary',async()=>{
 const configuration={appsFeatureEnabled:false,pluginsFeatureEnabled:false,configuredMcpServers:['alpha'],enabledMcpServers:[]};
 for(const responses of [
  {'app/installed':{apps:[{id:'connected-app'}]},'mcpServerStatus/list':{data:[]}},
  {'app/installed':{apps:[]},'mcpServerStatus/list':{data:[{name:'alpha',runtimeStatus:null,serverInfo:null,toolsError:null,tools:{write:{}},resources:[],resourceTemplates:[]}]}},
 ]) {
  const {request}=requestBoundary(responses);
  await assert.rejects(verifyExternalToolIsolation(request,'thread-1',configuration),/External app-server tools are not isolated/);
 }
});
