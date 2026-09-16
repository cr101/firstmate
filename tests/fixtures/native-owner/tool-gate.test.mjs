import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {PassThrough} from 'node:stream';
import {fileURLToPath} from 'node:url';
import {createNotificationGate} from '../../../bin/native-owner/codex-tool-gate.mjs';
import {runCodexHost} from '../../../bin/native-owner/codex-host-runtime.mjs';
import {createFakeAppServer} from './fake-app-server.mjs';
const message={receipt:'receipt',challenge:'observed',message:'Controlled message',checkpointExit:124};
function fixture(operate) {
 const calls=[];let alive=true;
 const gate=createNotificationGate({primaryThread:'primary',isAlive:()=>alive,operate:async(...args)=>{
  calls.push(args);
  return operate?operate(...args):args[0]==='check'?{operationState:'delivered',notification:message}:{operationState:'acknowledged'};
 }});
 gate.beginTurn('primary','turn');
 return {gate,calls,die:()=>{alive=false;}};
}
const check=(overrides={})=>({threadId:'primary',turnId:'turn',callId:'check',namespace:null,tool:'fm_notification_check',arguments:{},...overrides});
const ack=(overrides={})=>check({callId:'ack',tool:'fm_notification_ack',arguments:{receipt:'receipt',observed:'observed'},...overrides});

test('quiet checks create no receipt and permit a later delivery',async()=>{
 let count=0;const {gate,calls}=fixture(()=>++count===1?{operationState:'quiet'}:{operationState:'delivered',notification:message});
 assert.deepEqual((await gate.handle(check())).value,{quiet:true});
 assert.equal((await gate.handle(ack())).success,false);
 assert.equal((await gate.handle(check({callId:'later'}))).value.receipt,'receipt');
 assert.equal(calls.length,2);
});
test('startup in progress is explicit and a queued note remains deliverable',async()=>{
 let count=0;const {gate,calls}=fixture(()=>++count===1?{operationState:'starting'}:{operationState:'delivered',notification:message});
 assert.deepEqual(await gate.handle(check()),{success:false,value:{unavailable:'startup-in-progress'}});
 const delivered=await gate.handle(check({callId:'after-startup'}));
 assert.equal(delivered.success,true);assert.equal(delivered.value.receipt,'receipt');assert.equal(calls.length,2);
});
test('a cancelled quiet check cannot authorize another turn',async()=>{
 let finish;const {gate}=fixture(()=>new Promise(resolve=>{finish=resolve;}));
 const pending=gate.handle(check());gate.endTurn('primary','turn');finish({operationState:'quiet'});
 assert.equal((await pending).success,false);
});
test('observation precedes one acknowledgement; receipt replay cannot execute twice',async()=>{
 const {gate,calls}=fixture();
 assert.equal((await gate.handle(ack())).success,false);
 assert.equal(calls.length,0);
 assert.equal((await gate.handle(check())).success,true);
 assert.equal((await gate.handle(ack({callId:'valid-ack'}))).success,true);
 assert.equal((await gate.handle(ack({callId:'replayed-receipt'}))).value.denied,'receipt-already-consumed');
 assert.deepEqual(calls,[['check'],['ack',{receipt:'receipt',observed:'observed'}]]);
});
for(const [name,request] of Object.entries({
 foreignThread:check({threadId:'foreign'}), foreignTurn:check({turnId:'foreign'}),
 emptyCall:check({callId:''}), namespace:check({namespace:'other'}),
 arbitraryCommand:check({arguments:{command:'write files'}}), forgedIdentity:check({arguments:{threadId:'primary'}}),
 unknownTool:check({tool:'shell'}), nullArguments:check({arguments:null}), arrayArguments:check({arguments:[]}),
}))test(`${name} cannot reach the native operation`,async()=>{
 const {gate,calls}=fixture();assert.equal((await gate.handle(request)).success,false);assert.equal(calls.length,0);
});
test('duplicate call ID rejected independently of receipt state',async()=>{
 const {gate,calls}=fixture();await gate.handle(check());
 assert.equal((await gate.handle(check())).value.denied,'wrong-thread-turn-or-replay');assert.equal(calls.length,1);
});
test('wrong observation, receipt, and extra arguments cannot acknowledge',async()=>{
 const {gate,calls}=fixture();await gate.handle(check());
 for(const [i,args] of [{receipt:'wrong',observed:'observed'},{receipt:'receipt',observed:'wrong'},{...message,command:'other'}].entries())assert.equal((await gate.handle(ack({callId:'bad'+i,arguments:args}))).success,false);
 assert.equal(calls.length,1);
});
test('dead connection and completed turn deny even with correct IDs',async()=>{
 const f=fixture();f.die();assert.equal((await f.gate.handle(check())).success,false);assert.equal(f.calls.length,0);
 const g=fixture();g.gate.endTurn('primary','turn');assert.equal((await g.gate.handle(check())).success,false);assert.equal(g.calls.length,0);
});
test('completed turn cannot be resurrected',()=>{
 const {gate}=fixture();gate.endTurn('primary','turn');assert.throws(()=>gate.beginTurn('primary','turn'));
});
test('next cycle works without reauthorizing an earlier receipt',async()=>{
 let cycle=0;
 const {gate,calls}=fixture(action=>action==='check'?{operationState:'delivered',notification:{...message,receipt:'receipt'+(++cycle)}}:{operationState:'acknowledged'});
 await gate.handle(check());
 assert.equal((await gate.handle(ack({arguments:{receipt:'receipt1',observed:'observed'}}))).success,true);
 gate.endTurn('primary','turn');gate.beginTurn('primary','turn2');
 assert.equal((await gate.handle(check({turnId:'turn2'}))).success,true);
 assert.equal((await gate.handle(ack({turnId:'turn2',arguments:{receipt:'receipt1',observed:'observed'}}))).success,false);
 assert.equal((await gate.handle(ack({turnId:'turn2',callId:'second-ack',arguments:{receipt:'receipt2',observed:'observed'}}))).success,true);
 assert.equal(calls.length,4);
});
test('closed gate cannot be rebound to another thread',()=>{
 const {gate}=fixture();assert.throws(()=>gate.beginTurn('foreign','turn2'));gate.close();assert.throws(()=>gate.beginTurn('primary','turn2'));
});
test('concurrent calls cannot start overlapping operations',async()=>{
 let finish;const {gate,calls}=fixture(()=>new Promise(resolve=>{finish=resolve;}));
 const first=gate.handle(check());
 assert.equal((await gate.handle(check({callId:'concurrent'}))).value.denied,'operation-in-progress');
 finish({operationState:'delivered',notification:message});assert.equal((await first).success,true);assert.equal(calls.length,1);
});
test('late delivery after turn completion grants no further action',async()=>{
 let finish;const {gate,calls}=fixture(()=>new Promise(resolve=>{finish=resolve;}));
 const first=gate.handle(check());gate.endTurn('primary','turn');finish({operationState:'delivered',notification:message});
 assert.equal((await first).success,false);assert.equal((await gate.handle(ack())).success,false);assert.equal(calls.length,1);
});
test('pending delivery can be reread without a new native operation',async()=>{
 const {gate,calls}=fixture();const first=await gate.handle(check());first.value.receipt='caller-change';
 const second=await gate.handle(check({callId:'redelivery'}));
 assert.equal(second.value.receipt,'receipt');assert.equal(calls.length,1);
});
test('notification arriving after cancellation remains available next turn',async()=>{
 let finish;const {gate,calls}=fixture(()=>new Promise(resolve=>{finish=resolve;}));
 const first=gate.handle(check());gate.endTurn('primary','turn');finish({operationState:'delivered',notification:message});
 assert.equal((await first).success,false);gate.beginTurn('primary','next');
 const recovered=await gate.handle(check({turnId:'next',callId:'redelivery'}));
 assert.equal(recovered.success,true);assert.equal(recovered.value.receipt,'receipt');assert.equal(calls.length,1);
});
test('operation failure is not reported as success and cannot be blindly retried',async()=>{
  const {gate,calls}=fixture(()=>{throw Error('partial operation requires reconciliation');});
  assert.equal((await gate.handle(check())).success,false);assert.equal((await gate.handle(check({callId:'retry'}))).success,false);assert.equal(calls.length,1);
});
const repo=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../../..');
async function hostScenario(scenario){
 const area=fs.mkdtempSync(path.join(os.tmpdir(),'fm-native-host-loop-'));
 const runtime=path.join(area,'runtime'),home=path.join(area,'home');
 fs.mkdirSync(runtime,{recursive:true});fs.mkdirSync(home,{recursive:true});
 fs.writeFileSync(path.join(runtime,'digest.ready'),'ready\n');
 fs.writeFileSync(path.join(runtime,'startup.log'),'deterministic startup digest\n');
 fs.writeFileSync(path.join(runtime,'startup.finished'),'finished\n');
 const input=new PassThrough(),output=new PassThrough(),error=new PassThrough();
 const notification={receipt:'receipt',challenge:'observed',message:'Controlled message',checkpointExit:124};
 let ackPending=false,acknowledgements=0,shutdowns=0,afterShutdown=0;
 const native=async(action,extra)=>{
  if(shutdowns&&action!=='shutdown')afterShutdown++;
  if(action==='status')return {operationState:'ready'};
  if(action==='result'){
   if(ackPending){ackPending=false;return {operationState:'acknowledged'};}
   return {operationState:'delivered',notification};
  }
  if(action==='ack'){
   assert.deepEqual(extra,{receipt:'receipt',observed:'observed'});
   acknowledgements++;ackPending=true;return {operationState:'pending'};
  }
  if(action==='shutdown'){shutdowns++;return {operationState:'stopped',reconciliationRequired:false};}
  throw Error('unexpected native action '+action);
 };
 let result,failure;
 try {
  result=await runCodexHost({
   env:{...process.env,FM_PROBE_HOME:runtime,FM_PROBE_CODE_ROOT:repo,FM_HOME:home,FM_PROBE_SESSION:'session',FM_PROBE_NONCE:'nonce'},
   input,output,error,native,mcpServerNames:[],spawnAppServer:()=>createFakeAppServer(scenario),installSignalHandlers:false,
   afterAutomaticTurn:()=>{
    setTimeout(()=>input.write('/quit\n'),20);
    return true;
   },
  });
 }catch(errorValue){failure=errorValue;}
 return {result,failure,acknowledgements,shutdowns,afterShutdown,host:JSON.parse(fs.readFileSync(path.join(runtime,'host.json'),'utf8'))};
}

for(const scenario of ['prose','denied','malformed'])test(`actual host loop preserves a ${scenario} completed turn`,async()=>{
 const result=await hostScenario(scenario);
 assert.match(result.failure?.message??'',/durable work was preserved/);
 assert.equal(result.acknowledgements,0);
 assert.equal(result.shutdowns,1);
 assert.equal(result.afterShutdown,0);
 assert.deepEqual(result.host.turns.map(turn=>turn.status),['completed']);
 if(scenario==='prose')assert.equal(result.host.tools.length,0);
 else assert.equal(result.host.tools[0].success,false);
});

test('actual host loop suppresses only the exact successfully offered receipt',async()=>{
 const result=await hostScenario('success');
 assert.equal(result.failure,undefined);
 assert.equal(result.acknowledgements,1);
 assert.equal(result.shutdowns,1);
 assert.equal(result.afterShutdown,0);
 assert.deepEqual(result.result.automatic.map(item=>item.outcome),['offered']);
 assert.deepEqual(result.host.turns.map(turn=>turn.status),['completed']);
 assert.deepEqual(result.host.tools.map(tool=>[tool.tool,tool.success]),[
  ['fm_notification_check',true],['fm_notification_ack',true],
 ]);
});
