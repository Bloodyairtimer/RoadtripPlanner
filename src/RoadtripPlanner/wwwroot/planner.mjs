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
  return JSON.stringify({points:routeNodes(trip).map(p => [p.latitude,p.longitude]),camper:trip?.camper ?? null});
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

export function campsiteSources(camp) {
  const source = camp.source ?? 'OpenStreetMap';
  const fallback = source === 'Park4Night' && /^park4night\/[1-9]\d*$/.test(camp.id)
    ? `https://park4night.com/en/place/${camp.id.split('/')[1]}`
    : source === 'OpenStreetMap' && /^(node|way|relation)\/[1-9]\d*$/.test(camp.id)
      ? `https://www.openstreetmap.org/${camp.id}` : null;
  const links = [{name:source,url:camp.sourceUrl ?? fallback},...(camp.otherSources ?? [])];
  const seen = new Set();
  return links.filter(link => {
    if (!['Park4Night','OpenStreetMap'].includes(link.name)) return false;
    try {
      const url = new URL(link.url);
      if (!['https:','http:'].includes(url.protocol) || seen.has(url.href)) return false;
      seen.add(url.href);return true;
    } catch { return false; }
  });
}
export function campsiteType(camp) {
  return camp.category ?? ({camp_site:'Camping',caravan_site:'Camper / caravan site',overnight_parking:'Parking day/night',nature_spot:'Surrounded by nature'}[camp.type] ?? 'Place to stay');
}
export function matchesCampsite(camp,type = 'all',source = 'all',query = '') {
  const typeMatches = type === 'all' ? ['camp_site','caravan_site'].includes(camp.type)
    : type === 'any' ? true : type === 'other' ? ['overnight_parking','nature_spot'].includes(camp.type) : camp.type === type;
  return typeMatches && (source === 'all' || (camp.source ?? 'OpenStreetMap') === source || campsiteSources(camp).some(s => s.name === source))
    && camp.name.toLowerCase().includes(query.trim().toLowerCase());
}

export function defaultCamperProfile() {
  return {lengthM:7.48,widthM:2.30,heightM:2.98,weightKg:3500,maxSpeedKmh:120};
}
