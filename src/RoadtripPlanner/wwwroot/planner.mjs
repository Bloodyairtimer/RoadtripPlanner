// Route endpoints are separate from itinerary stops, so reordering never moves home.
export function routeNodes(trip) {
  if (!trip) return [];
  const nodes = [];
  if (trip.startLocation) nodes.push({...trip.startLocation, key:'start', kind:'start', stopIndex:-1});
  for (const [i, stop] of trip.stops.entries()) nodes.push({...stop, key:stop.id, kind:'stop', stopIndex:i});
  if (trip.endLocation) nodes.push({...trip.endLocation, key:'end', kind:'end', stopIndex:trip.stops.length});
  return nodes;
}
export function routeSignature(trip) {
  return JSON.stringify(routeNodes(trip).map(p => [p.latitude,p.longitude]));
}
export function insertionIndex(trip, legIndex) {
  const nodes = routeNodes(trip);
  if (!Number.isInteger(legIndex) || legIndex < 0 || legIndex >= nodes.length-1) throw new RangeError('Choose an existing route leg.');
  return Math.min(trip.stops.length, Math.max(0, nodes[legIndex+1].stopIndex));
}
export function formatDriving(hours) {
  const minutes = Math.round(hours * 60);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes/60)}h ${minutes%60 ? `${minutes%60}m` : ''}`.trim();
}
