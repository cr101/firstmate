// Actual launcher integration, not the fixture controller or a scripted model.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import {fileURLToPath} from 'node:url';
import {spawn,spawnSync} from 'node:child_process';
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../../..');
const area=fs.mkdtempSync(path.join(os.tmpdir(),'fm-launcher-e2e-')),code=path.join(area,'code');
const image=process.env.FM_NATIVE_TEST_JQ_IMAGE;
if(!image)throw Error('FM_NATIVE_TEST_JQ_IMAGE must name an existing local image with jq and GNU timeout');
const read=file=>JSON.parse(fs.readFileSync(file,'utf8').replace(/^\uFEFF/,''));
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function command(exe,args){const result=spawnSync(exe,args,{encoding:'utf8',timeout:120000});if(result.status!==0)throw Error(result.stderr||result.stdout);return result;}
command('git',['-c','core.symlinks=true','clone','--quiet','--no-local','--single-branch',repo,code]);
fs.cpSync(path.join(repo,'bin/native-owner'),path.join(code,'bin/native-owner'),{recursive:true});
for(const name of ['fm-native-codex.ps1','fm-session-lock-lib.sh','fm-sessionstart-nudge.sh','fm-harness.sh'])fs.copyFileSync(path.join(repo,'bin',name),path.join(code,'bin',name));
const launcher=path.join(code,'bin/fm-native-codex.ps1');
command('powershell.exe',['-NoProfile','-File',launcher,'-BuildOnly']);
const removedAlias=spawnSync('powershell.exe',['-NoProfile','-File',launcher,'-BuildOnly','-Home',path.join(area,'alias')],{encoding:'utf8',timeout:120000});
assert.notEqual(removedAlias.status,0,'The removed -Home alias was still accepted');
function start(home,verify=true){
 const args=['-NoProfile','-File',launcher,'-Experimental','-OperationalHome',home,'-JqImage',image];if(verify)args.push('-VerifyOnly');
 const child=spawn('powershell.exe',args,{stdio:['pipe','pipe','pipe']});let stdout='',stderr='';
 child.stdout.on('data',data=>stdout+=data);child.stderr.on('data',data=>stderr+=data);child.stdin.on('error',()=>{});
 const done=new Promise((resolve,reject)=>{child.on('error',reject);child.on('exit',exit=>resolve({exit,stdout,stderr}));});
 return {child,done,home};
}
async function bound(promise,session,ms=230000){let timer;try{return await Promise.race([promise,new Promise((_,reject)=>{timer=setTimeout(()=>{session.child.stdin.write('/quit\n');reject(Error('Launcher exceeded its bound; inspect '+session.home));},ms);})]);}finally{clearTimeout(timer);}}
async function ready(session,previous){
 for(let i=0;i<2200;i++){
  if(session.child.exitCode!==null)throw Error(JSON.stringify(await session.done));
  try {const owner=read(path.join(session.home,'owner-probe.json'));if(owner.state==='live'&&typeof owner.generation==='string'&&owner.generation!==previous){const runtime=path.join(session.home,'state/native-runtime',owner.generation);if(read(path.join(runtime,'host.json')).ready)return {owner,runtime};}}catch(error){if(error.code!=='ENOENT'&&!(error instanceof SyntaxError))throw error;}
  await sleep(100);
 }
 throw Error('Launcher readiness timed out');
}
const records=[];
const live=process.argv.includes('--live');
if(live&&process.env.FM_LIVE_NATIVE_CODEX!=='1')throw Error('Live launcher tests require FM_LIVE_NATIVE_CODEX=1');
const posix=value=>value.replaceAll('\\','/').replace(/^([A-Za-z]):/,(_,drive)=>'/'+drive.toLowerCase());
function enqueue(home,message){
 fs.mkdirSync(home,{recursive:true});
 const env=Object.fromEntries(Object.entries(process.env).filter(([key])=>!key.startsWith('FM_')&&!key.startsWith('PI_')));
 Object.assign(env,{FM_HOME:posix(home),FM_PROBE_JQ_IMAGE:image,MSYS:'winsymlinks:nativestrict'});
 const result=spawnSync('C:/Program Files/Git/bin/bash.exe',['--noprofile','--norc','-c','export PATH="$1/bin/native-owner/tools:/usr/bin:/bin:$PATH"; export FM_HOME; FM_HOME=$(cygpath -u "$3"); exec /usr/bin/bash "$1/bin/fm-inbox.sh" note "$2"','launcher-test',posix(code),message,home],{env,encoding:'utf8',timeout:30000});
 assert.equal(result.status,0,JSON.stringify({error:result.error?.message,stdout:result.stdout,stderr:result.stderr}));return result.stdout.trim().split(/\s+/)[1];
}
const home=path.join(area,'home');
const first=start(home);const initial=await ready(first);console.error('first ready',area);
const competitor=start(home);competitor.child.stdin.end();const refused=await bound(competitor.done,competitor,20000);
console.error('competitor returned',refused);assert.notEqual(refused.exit,0);assert.equal(read(path.join(home,'owner-probe.json')).generation,initial.owner.generation);
console.error('quitting first');first.child.stdin.write('/quit\n');assert.equal((await bound(first.done,first,20000)).exit,0);
assert.equal(read(path.join(initial.runtime,'shutdown.json')).stopped,true);
records.push('exclusive launch and confirmed shutdown');
const startup=path.join(code,'bin/native-owner/startup.sh'),startupSource=fs.readFileSync(startup,'utf8');
fs.writeFileSync(startup,'#!/usr/bin/env bash\nexit 7\n');
const failed=start(home);failed.child.stdin.end();assert.notEqual((await bound(failed.done,failed,20000)).exit,0);
assert.equal(fs.readFileSync(path.join(home,'state/.lock'),'utf8').trim(),'native:'+initial.owner.generation);
fs.writeFileSync(startup,startupSource);
const again=start(home);const restarted=await ready(again,initial.owner.generation);assert.notEqual(restarted.owner.generation,initial.owner.generation);
assert.equal(fs.readFileSync(path.join(home,'state/.lock'),'utf8').trim(),'native:'+restarted.owner.generation);
again.child.stdin.end();assert.equal((await bound(again.done,again,20000)).exit,0);
records.push('restart after an intervening failed startup retains proven-dead ownership history');
const populated=path.join(area,'populated');fs.mkdirSync(path.join(populated,'state'),{recursive:true});fs.writeFileSync(path.join(populated,'state/work.meta'),'preserve');
const blocked=start(populated);blocked.child.stdin.end();assert.notEqual((await bound(blocked.done,blocked,20000)).exit,0);
assert.equal(fs.readFileSync(path.join(populated,'state/work.meta'),'utf8'),'preserve');assert.equal(fs.existsSync(path.join(populated,'owner-probe.json')),false);
records.push('populated home refused without changing its records');
for(const [name,relative] of [['relay-config','config/x-mode.env'],['relay-watch','state/x-watch.check.sh']]){
 const relayHome=path.join(area,name),record=path.join(relayHome,relative),contents='preserve relay state';
 fs.mkdirSync(path.dirname(record),{recursive:true});fs.writeFileSync(record,contents);
 const refusedRelay=start(relayHome);refusedRelay.child.stdin.end();assert.notEqual((await bound(refusedRelay.done,refusedRelay,20000)).exit,0);
 assert.equal(fs.readFileSync(record,'utf8'),contents);assert.equal(fs.existsSync(path.join(relayHome,'owner-probe.json')),false);
}
records.push('generated Relay state refused before lease acquisition and preserved');
const outside=path.join(repo,'data/native-launcher',path.basename(area)+'-outside');
assert.equal(fs.existsSync(outside),false);
const external=start(outside);external.child.stdin.end();assert.notEqual((await bound(external.done,external,20000)).exit,0);assert.equal(fs.existsSync(outside),false);
const target=path.join(area,'junction-target'),junction=path.join(area,'junction');fs.mkdirSync(target);fs.symlinkSync(target,junction,'junction');
const linked=start(path.join(junction,'home'));linked.child.stdin.end();assert.notEqual((await bound(linked.done,linked,20000)).exit,0);assert.equal(fs.existsSync(path.join(target,'home')),false);
records.push('non-temporary and pre-existing reparse-point homes refused before creation');
// Delay the existing network owner only in this disposable code copy. The
// production launcher has no delay/mock switch and still invokes that owner.
const network=path.join(code,'bin/fm-startup-network.sh');fs.renameSync(network,network+'.actual');
fs.writeFileSync(network,'#!/usr/bin/env bash\nif [ "${1:-}" = run ]; then sleep 15; fi\nexec "$(dirname "$0")/fm-startup-network.sh.actual" "$@"\n');
// Its start command invokes the original script's self path. Delay the worker
// entry inside that preserved copy, before any original script logic executes.
const original=fs.readFileSync(network+'.actual','utf8');fs.writeFileSync(network+'.actual',original.replace(/^#![^\n]*\n/,'#!/usr/bin/env bash\nif [ "${1:-}" = run ]; then sleep 15; fi\n'));
const deferred=start(path.join(area,'deferred'));const early=await ready(deferred);
const earlyEvidence=read(path.join(early.runtime,'host.json'));
assert.equal(earlyEvidence.digestDeliveredBeforeDeferred,true);
const independent=spawn(process.execPath,['-e','setTimeout(()=>{},60000)'],{stdio:'ignore'});
try {
 deferred.child.stdin.write('/quit\n');assert.equal((await bound(deferred.done,deferred,20000)).exit,0);
 assert.equal(read(path.join(early.runtime,'shutdown.json')).stopped,true);assert.equal(independent.exitCode,null);
 records.push('digest delivered before deferred completion; cancellation preserves an independent process');
}finally{independent.kill();}
fs.writeFileSync(network,original);fs.unlinkSync(network+'.actual');
if(live){
 const messageHome=path.join(area,'live-message');
 const model=start(messageHome,false);const liveReady=await ready(model);const runtime=liveReady.runtime;
 const notes=[];
 for(let cycle=1;cycle<=2;cycle++){
  notes.push(enqueue(messageHome,'Launcher integration notification '+cycle+': no project action is requested. Read and acknowledge this notification.'));
  let completed=false;
  for(let i=0;i<1800;i++){
   if(model.child.exitCode!==null)throw Error(JSON.stringify(await model.done));
   const evidence=read(path.join(runtime,'host.json'));
   if(evidence.turns.length>=cycle&&evidence.turns[cycle-1].status==='completed'){completed=true;break;}
   await sleep(100);
  }
  assert(completed,'The notification did not produce a completed model turn');
 }
 model.child.stdin.write('/quit\n');const result=await bound(model.done,model,20000);assert.equal(result.exit,0,result.stderr);
 const host=read(path.join(runtime,'host.json'));assert.equal(host.turns.length,2);
 assert(host.tools.some(tool=>tool.tool==='fm_notification_check'&&tool.success));assert(host.tools.some(tool=>tool.tool==='fm_notification_ack'&&tool.success));
 for(const note of notes)assert(fs.existsSync(path.join(messageHome,'state/inbox/handled',note+'.note')));
  assert.equal(fs.readFileSync(path.join(messageHome,'state/.wake-queue'),'utf8').trim(),'');
  fs.writeFileSync(path.join(runtime,'console.json'),JSON.stringify(result,null,2));
  records.push('two real post-startup notification cycles were automatically delivered, observed, and acknowledged');
  const retryHome=path.join(area,'live-interrupted-redelivery');
  const retryNote=enqueue(retryHome,'Interrupted automatic handling test: read and acknowledge this notification when handling resumes.');
  const retry=start(retryHome,false);const retryReady=await ready(retry);let interruptSent=false,interrupted=false,completed=false;
  for(let i=0;i<1800;i++){
   if(retry.child.exitCode!==null)throw Error(JSON.stringify(await retry.done));
   const host=read(path.join(retryReady.runtime,'host.json'));
   if(host.activeTurn){retry.child.stdin.write('/interrupt\n');interruptSent=true;break;}
   await sleep(50);
  }
  assert(interruptSent,'No automatic notification turn became active for interruption');
  for(let i=0;i<1800;i++){
   if(retry.child.exitCode!==null)throw Error(JSON.stringify(await retry.done));
   const turns=read(path.join(retryReady.runtime,'host.json')).turns;
   interrupted=turns.some(turn=>turn.status==='interrupted');completed=interrupted&&turns.some(turn=>turn.status==='completed');
   if(completed&&fs.existsSync(path.join(retryHome,'state/inbox/handled',retryNote+'.note')))break;
   await sleep(100);
  }
  assert(interrupted,'The automatic notification turn was not interrupted');
  assert(completed,'The interrupted notification was not offered to a later automatic turn');
  await sleep(1500);
  assert.deepEqual(read(path.join(retryReady.runtime,'host.json')).turns.map(turn=>turn.status),['interrupted','completed']);
  retry.child.stdin.write('/quit\n');const retryResult=await bound(retry.done,retry,20000);assert.equal(retryResult.exit,0,retryResult.stderr);
  assert.equal(read(path.join(retryReady.runtime,'host.json')).turns.length,2);
  assert.equal(read(path.join(retryReady.runtime,'shutdown.json')).stopped,true);
  assert.equal(fs.readFileSync(path.join(retryHome,'state/.wake-queue'),'utf8').trim(),'');
  records.push('interrupted automatic handling re-offered the pending receipt once, then stopped after completion and quit');
  const cancelHome=path.join(area,'live-cancel');const pendingNote=enqueue(cancelHome,'Cancellation test: leave this notification pending; do not acknowledge it.');
 const active=start(cancelHome,false);active.child.stdin.write('Call fm_notification_check once, but do not acknowledge anything. Explain what remains pending. Do not use other tools.\n');
 const current=await ready(active);let began=false;
 for(let i=0;i<1200;i++){if(read(path.join(current.runtime,'host.json')).activeTurn){began=true;break;}await sleep(50);}
 assert(began,'No active turn to cancel');active.child.stdin.write('/quit\n');
 const stopped=await bound(active.done,active,20000);assert.equal(stopped.exit,0,stopped.stderr);
 assert(read(path.join(current.runtime,'host.json')).turns.some(turn=>turn.status==='interrupted'));
 assert.equal(read(path.join(current.runtime,'shutdown.json')).stopped,true);
 assert(fs.existsSync(path.join(cancelHome,'state/inbox',pendingNote+'.note')));
 records.push('shutdown interrupted a real active turn and preserved its pending notification');
}
const state=path.join(repo,'data/native-launcher');fs.mkdirSync(state,{recursive:true});
const summary=JSON.stringify({area,code,home,records,live,passed:true},null,2);
fs.writeFileSync(path.join(state,'launcher-latest.json'),summary);
fs.writeFileSync(path.join(state,live?'launcher-live-latest.json':'launcher-smoke-latest.json'),summary);
console.log('PASS: '+records.join('; '));
