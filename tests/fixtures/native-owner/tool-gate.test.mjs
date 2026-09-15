import test from 'node:test';
import assert from 'node:assert/strict';
import {createNotificationGate} from '../../../bin/native-owner/codex-tool-gate.mjs';
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
