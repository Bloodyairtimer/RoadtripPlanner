import {test} from 'node:test';
import assert from 'node:assert/strict';
import {routeNodes,routeSignature,insertionIndex,formatDriving} from '../../src/RoadtripPlanner/wwwroot/planner.mjs';
const home = {name:'Home',latitude:52.3,longitude:5.6};
const finish = {name:'Finish',latitude:51,longitude:4};
const stops = [{id:'a',name:'Milan',latitude:45.4,longitude:9.1,nights:2},{id:'b',name:'Nice',latitude:43.7,longitude:7.2,nights:3}];
test('start and finish surround highlights in their planned order', () => {
  assert.deepEqual(routeNodes({startLocation:home,endLocation:finish,stops}).map(n => n.name),['Home','Milan','Nice','Finish']);
});
test('round trips retain both home endpoints when highlights are reordered', () => {
  assert.deepEqual(routeNodes({startLocation:home,endLocation:home,stops:[...stops].reverse()}).map(n => n.key),['start','b','a','end']);
});
test('insert before first highlight, between highlights, and before finish', () => {
  const trip = {startLocation:home,endLocation:finish,stops};
  assert.deepEqual([0,1,2].map(i => insertionIndex(trip,i)),[0,1,2]);
});
test('a direct start-to-finish route accepts the first intermediate stop', () => {
  assert.equal(insertionIndex({startLocation:home,endLocation:finish,stops:[]},0),0);
});
test('older trips without endpoint fields preserve their stops', () => {
  assert.deepEqual(routeNodes({stops}).map(n => n.name),['Milan','Nice']);
  assert.equal(insertionIndex({stops},0),1);
});
test('coordinates and order invalidate routing; notes and nights do not', () => {
  const trip = {startLocation:home,endLocation:finish,stops};
  const original = routeSignature(trip);
  assert.equal(routeSignature({...trip,notes:'New',stops:stops.map(s => ({...s,nights:5,notes:'Ideas'}))}),original);
  assert.notEqual(routeSignature({...trip,stops:[...stops].reverse()}),original);
  assert.notEqual(routeSignature({...trip,endLocation:home}),original);
});
test('invalid insertion positions cannot move a stop outside the itinerary', () => {
  for (const i of [-1,2,1.2,NaN]) assert.throws(() => insertionIndex({stops},i),RangeError);
});
test('driving duration rounds minutes before formatting', () => {
  assert.equal(formatDriving(1.999),'2h');
  assert.equal(formatDriving(.5),'30 min');
  assert.equal(formatDriving(5.25),'5h 15m');
});
