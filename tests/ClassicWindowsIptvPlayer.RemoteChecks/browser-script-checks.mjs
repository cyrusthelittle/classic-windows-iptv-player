import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const html=readFileSync(process.argv[2],'utf8');
const buttons=[...html.matchAll(/data-group="([^"]+)" data-value="(\d+)"/g)].map(([,group,value])=>({group,dataset:{value},active:false,pressed:undefined,classList:{toggle(_,active){this.owner.active=active}},setAttribute(_,value){this.pressed=value}}));
for(const button of buttons)button.classList.owner=button;
const requests=[];let inFlight=0,maxInFlight=0,commandInFlight=0,maxCommandInFlight=0;const alerts=[];
const context={document:{querySelectorAll(selector){return buttons.filter(b=>selector.includes('"'+b.group+'"'))}},fetch:async url=>{requests.push(url);if(url.startsWith('/cmd')){commandInFlight++;maxCommandInFlight=Math.max(maxCommandInFlight,commandInFlight)}inFlight++;maxInFlight=Math.max(maxInFlight,inFlight);await new Promise(r=>setTimeout(r,2));inFlight--;if(url.startsWith('/cmd'))commandInFlight--;return{ok:true,json:async()=>({browseMode:1,mediaKindMode:2,viewMode:1})}},setTimeout,clearTimeout,setInterval(){},alert:message=>alerts.push(message),encodeURIComponent,Promise};
vm.createContext(context);vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1],context);
await vm.runInContext('syncState()',context);
assert.equal(buttons.filter(b=>b.active).length,3);
assert.deepEqual(buttons.filter(b=>b.active).map(b=>[b.group,b.dataset.value]),[['browse','1'],['media','2'],['view','1']]);
assert.equal(buttons.filter(b=>b.pressed===true).length,3);
await vm.runInContext("queueSearch('Old');queueSearch('Beta + & :');cmd('down');cmd('select');commandQueue",context);
const commands=requests.filter(u=>u.startsWith('/cmd'));
assert.equal(commands.length,3);assert.match(commands[0],/name=search&value=Beta%20%2B%20%26%20%3A/);assert.match(commands[1],/name=down/);assert.match(commands[2],/name=select/);assert.equal(maxInFlight<=2,true);assert.equal(maxCommandInFlight,1);
assert.equal(alerts.length,0);
// A later PC-side change replaces the prior active markers, including clearing
// every previously pressed button. A failed poll retains the last known state.
context.fetch=async()=>({ok:true,json:async()=>({browseMode:2,mediaKindMode:3,viewMode:2})});
await vm.runInContext('syncState()',context);
assert.deepEqual(buttons.filter(b=>b.active).map(b=>[b.group,b.dataset.value]),[['browse','2'],['media','3'],['view','2']]);
assert.equal(buttons.filter(b=>b.pressed===true).length,3);
assert.equal(buttons.filter(b=>b.pressed===false).length,7);
context.fetch=async()=>({ok:false,status:503});await vm.runInContext('syncState()',context);
assert.deepEqual(buttons.filter(b=>b.active).map(b=>[b.group,b.dataset.value]),[['browse','2'],['media','3'],['view','2']]);
context.fetch=async()=>({ok:false,status:400});await vm.runInContext("cmd('invalid');commandQueue",context);assert.match(alerts[0],/HTTP 400/);
// A failed command must not permanently poison the serialized queue.
const recovered=[];context.fetch=async url=>{recovered.push(url);return{ok:true,json:async()=>({browseMode:2,mediaKindMode:3,viewMode:2})}};
await vm.runInContext("cmd('down');commandQueue",context);assert.equal(recovered.includes('/cmd?name=down'),true);
console.log('PASS active filters and aria-pressed follow PC changes; failed polls retain state; latest search flushed before ordered navigation; HTTP failures surfaced and queue recovers. DOM fixture only, no browser layout verification.');
