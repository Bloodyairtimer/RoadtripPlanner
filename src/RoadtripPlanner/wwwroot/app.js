import {routeNodes, routeSignature, insertionIndex, formatDriving, campsiteSources, campsiteType, matchesCampsite} from './planner.mjs';
const $ = id => document.getElementById(id);
const state = { trips: [], trip: null, selected: null, dirty: false, busy: false, camps: [], campStop: null, route: null, routeKey: null, routing: false, routeError: null, campWarnings: [], campError: null, campLoading: false, placeTarget: null, insertLeg: null, mapInsert: null };
let map, stopLayer, campLayer, lineLayer, toastTimer, dragId, placeRequest = 0, campRequest = 0, routeTimer, routeRequest = 0, routeController, queuedRouteKey;
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const clone = value => structuredClone(value);
const uid = () => globalThis.crypto?.randomUUID?.() ?? 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.floor(Math.random()*16); return (c === 'x' ? r : (r & 3 | 8)).toString(16); });
const safeUrl = value => { try { const u = new URL(value); return ['https:', 'http:'].includes(u.protocol) ? u.href : null; } catch { return null; } };
const dateAt = (date, days) => { const d = new Date(`${date}T12:00:00Z`); d.setUTCDate(d.getUTCDate() + days); return d; };
const dateLabel = date => new Intl.DateTimeFormat(undefined, {day:'numeric',month:'short',year:'numeric',timeZone:'UTC'}).format(date);
const pointKey = () => routeSignature(state.trip);
const validRoute = () => state.route && state.routeKey === pointKey() ? state.route : null;

async function api(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { ...(options.body ? {'Content-Type':'application/json'} : {}), ...options.headers } });
  if (!response.ok) {
    let detail = response.status === 429 ? 'Too many searches. Please wait a minute and try again.' : `Request failed (${response.status}).`;
    try { const body = await response.json(); detail = body.detail || body.title || detail; } catch { /* Empty error response. */ }
    const error = new Error(detail); error.status = response.status; throw error;
  }
  return response.status === 204 ? null : response.json();
}
function notify(message, error = false) {
  clearTimeout(toastTimer); $('toast').textContent = message; $('toast').classList.toggle('error', error); $('toast').hidden = false;
  toastTimer = setTimeout(() => $('toast').hidden = true, error ? 10000 : 4500);
}
function dirty() { state.dirty = true; renderSave(); renderSummary(); scheduleRoute(); }
function renderSave() {
  $('save-state').textContent = state.busy ? 'Saving…' : state.dirty ? 'Unsaved changes' : 'All changes saved';
  $('save-state').classList.toggle('dirty', state.dirty);
  $('save-trip').disabled = !state.dirty || state.busy;
}
function canLeave() { return !state.dirty || confirm('You have unsaved changes. Discard them?'); }
async function busy(action) {
  if (state.busy) return;
  state.busy = true; renderSave();
  // Avoid editing while a server response could replace the current trip.
  $('workspace').inert = true; $('trip-form').inert = true; $('trip-list').inert = true;
  try { await action(); } catch (error) { notify(error.message, true); }
  finally { state.busy = false; $('workspace').inert = false; $('trip-form').inert = false; $('trip-list').inert = false; renderSave(); }
}
function renderSidebar() {
  $('trip-count').textContent = state.trips.length;
  $('trip-list').innerHTML = state.trips.map(t => `<button class="trip-link ${t.id === state.trip?.id ? 'active' : ''}" data-trip="${t.id}" ${t.id === state.trip?.id ? 'aria-current="page"' : ''}><span class="trip-icon">⌁</span><span class="trip-copy"><strong>${esc(t.name)}</strong><small>${t.stops.length} stops · ${t.stops.reduce((n,s) => n + s.nights, 0)} nights</small></span></button>`).join('');
  $('trip-list').querySelectorAll('[data-trip]').forEach(button => button.addEventListener('click', () => selectTrip(button.dataset.trip)));
}
function setTrip(trip) {
  clearTimeout(routeTimer); routeController?.abort(); queuedRouteKey = null; state.routing = false; state.routeError = null; state.mapInsert = null;
  state.trip = trip ? {startLocation:null,endLocation:null,...clone(trip)} : null; state.dirty = false; state.route = null; state.routeKey = null; state.camps = []; state.selected = trip?.stops[0]?.id ?? null;
  render(); renderMap(true); scheduleRoute();
  try { if (trip) localStorage.setItem('roam-trip', trip.id); else localStorage.removeItem('roam-trip'); } catch { /* Browser storage is optional. */ }
}
async function selectTrip(id) {
  if (state.busy || state.trip?.id === id || !canLeave()) return;
  await busy(async () => setTrip(await api(`/api/trips/${id}`)));
}
function render() {
  const trip = state.trip;
  $('welcome').hidden = !!trip; $('workspace').hidden = !trip; $('header-actions').hidden = !trip;
  $('trip-title').textContent = trip?.name ?? 'Your next adventure';
  if (trip) { $('trip-notes').value = trip.notes; renderEndpoints(); renderStops(); renderSummary(); }
  renderSidebar(); renderSave();
  if (map) requestAnimationFrame(() => map.invalidateSize());
}
function renderEndpoints() {
  $('start-label').textContent = state.trip.startLocation?.name ?? 'Choose your starting point';
  $('end-label').textContent = state.trip.endLocation?.name ?? 'Choose your finish';
  $('return-home').disabled = !state.trip.startLocation;
  $('clear-endpoints').disabled = !state.trip.startLocation && !state.trip.endLocation;
}
function renderSummary() {
  if (!state.trip) return;
  const t = state.trip, nights = t.stops.reduce((n,s) => n + Number(s.nights || 0), 0), count = routeNodes(t).length, route = validRoute();
  $('summary-stops').textContent = t.stops.length; $('summary-nights').textContent = nights;
  $('summary-date').textContent = t.startDate ? dateLabel(dateAt(t.startDate, 0)) : 'No date set';
  $('summary-end').textContent = t.startDate ? `Until ${dateLabel(dateAt(t.startDate, nights))}` : "Go whenever you're ready";
  $('calculate-route').disabled = count < 2 || state.routing;
  $('calculate-route').textContent = state.routing ? 'Routing…' : 'Update route';
  $('summary-distance').textContent = route ? `${Math.round(route.distanceKm).toLocaleString()} km` : state.routing ? 'Calculating…' : count < 2 ? 'Your route awaits' : state.routeError ? 'Route unavailable' : 'Ready to route';
  $('summary-duration').textContent = route ? `${formatDriving(route.durationHours)} driving · ${route.legs.length} legs` : count < 2 ? 'Set start, finish or highlights' : state.routeError ? 'Tap Update route to retry' : 'Road distances between every stop';
  $('route-note').textContent = route ? 'Driving distances follow the road between your pinned locations. Car estimates exclude breaks and traffic; check camper dimensions, ferries and road restrictions.' : state.routeError ?? 'Routes update automatically as you add or move places. Dotted lines are only a preview, not driving distances.';
}
function legMarkup(index) {
  const nodes = routeNodes(state.trip), route = validRoute(), leg = route?.legs[index];
  return `<div class="drive-leg ${leg?.durationHours > 4 ? 'long-drive' : ''}" data-leg-index="${index}"><div class="leg-line"><span>⌁</span><div><strong class="leg-distance">${leg ? `${Math.round(leg.distanceKm).toLocaleString()} km · ${formatDriving(leg.durationHours)}` : state.routing ? 'Calculating road distance…' : 'Road distance pending'}</strong><small>${esc(nodes[index].name)} → ${esc(nodes[index+1].name)}</small></div><button data-insert-leg="${index}" class="leg-insert" title="Add a stop between ${esc(nodes[index].name)} and ${esc(nodes[index+1].name)}">+ Add a stop</button></div></div>`;
}
function renderLegs() {
  $('stop-list').querySelectorAll('[data-leg-index]').forEach(row => {
    const leg = validRoute()?.legs[Number(row.dataset.legIndex)];
    row.classList.toggle('long-drive', !!leg && leg.durationHours > 4);
    row.querySelector('.leg-distance').textContent = leg ? `${Math.round(leg.distanceKm).toLocaleString()} km · ${formatDriving(leg.durationHours)}` : state.routing ? 'Calculating road distance…' : 'Road distance pending';
  });
}
function renderStops() {
  let days = 0;
  const nodes = routeNodes(state.trip);
  let html = state.trip.startLocation ? `<div class="route-endpoint"><span class="endpoint-dot start-dot"></span><div><small>DEPARTURE</small><strong>${esc(state.trip.startLocation.name)}</strong></div></div>` : '';
  html += state.trip.stops.map((s,i) => {
    const arrival = state.trip.startDate ? dateAt(state.trip.startDate, days) : null;
    days += s.nights;
    const nodeIndex = nodes.findIndex(n => n.key === s.id);
    return (nodeIndex > 0 ? legMarkup(nodeIndex-1) : '') + `<article class="stop-card ${s.id === state.selected ? 'selected' : ''}" data-stop="${s.id}"><span class="stop-number">${i+1}</span><div class="stop-top"><div><h3 tabindex="0" data-focus-stop="${s.id}">${esc(s.name)}</h3><div class="stop-date">${arrival ? esc(dateLabel(arrival)) : 'Date to be decided'} · ${s.nights === 0 ? 'Day visit' : `${s.nights} night${s.nights === 1 ? '' : 's'}`}</div></div><div class="reorder"><span class="drag-handle" draggable="true" aria-hidden="true" title="Drag to reorder">⠿</span><button data-action="edit" aria-label="Change ${esc(s.name)} location" title="Change location">✎</button><button data-action="up" aria-label="Move ${esc(s.name)} earlier" ${i === 0 ? 'disabled' : ''}>↑</button><button data-action="down" aria-label="Move ${esc(s.name)} later" ${i === state.trip.stops.length-1 ? 'disabled' : ''}>↓</button><button data-action="remove" aria-label="Remove ${esc(s.name)}">×</button></div></div><div class="stop-fields"><label class="nights-control" style="margin:0">☾ <input type="number" min="0" max="365" value="${s.nights}" data-field="nights" aria-label="Nights in ${esc(s.name)}"> nights</label><button class="camp-button" data-action="camp">♧ ${s.campsite ? 'Change campsite' : 'Find campsites'}</button></div>${s.campsite ? `<div class="selected-camp"><div><strong>♧ ${esc(s.campsite.name)}</strong><small>${s.campsite.distanceKm} km from stop · ${esc(campsiteType(s.campsite))}</small></div><button data-action="clear-camp" aria-label="Remove campsite">×</button></div>` : ''}<textarea class="stop-notes" rows="1" maxlength="5000" data-field="notes" aria-label="Notes for ${esc(s.name)}" placeholder="A little note for this stop…">${esc(s.notes)}</textarea></article>`;
  }).join('');
  if (state.trip.endLocation) {
    if (nodes.length > 1) html += legMarkup(nodes.length-2);
    html += `<div class="route-endpoint"><span class="endpoint-dot end-dot"></span><div><small>FINISH LINE</small><strong>${esc(state.trip.endLocation.name)}</strong></div></div>`;
  }
  if (!state.trip.stops.length) html += '<div class="empty-stops">Start with the highlights.<br>Add the places you want to see, then refine the journey with stops between them.</div>';
  $('stop-list').innerHTML = html;
  $('stop-list').querySelectorAll('[data-insert-leg]').forEach(b => b.addEventListener('click', () => openInsert(Number(b.dataset.insertLeg))));
  $('stop-list').querySelectorAll('[data-stop]').forEach(card => {
    const id = card.dataset.stop;
    card.querySelector('[data-focus-stop]').addEventListener('click', () => focusStop(id));
    card.querySelector('[data-focus-stop]').addEventListener('keydown', e => { if (e.key === 'Enter') focusStop(id); });
    card.querySelectorAll('[data-action]').forEach(b => b.addEventListener('click', () => stopAction(id, b.dataset.action)));
    card.querySelector('[data-field="notes"]').addEventListener('input', e => { state.trip.stops.find(s => s.id === id).notes = e.target.value; dirty(); });
    card.querySelector('[data-field="nights"]').addEventListener('change', e => {
      if (!e.target.checkValidity() || !Number.isInteger(e.target.valueAsNumber)) { e.target.reportValidity(); e.target.value = state.trip.stops.find(s => s.id === id).nights; return; }
      state.trip.stops.find(s => s.id === id).nights = e.target.valueAsNumber; dirty(); renderStops();
    });
    card.querySelector('.drag-handle').addEventListener('dragstart', e => { dragId = id; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', id); });
    card.querySelector('.drag-handle').addEventListener('dragend', () => { dragId = null; document.querySelectorAll('.drag-over').forEach(c => c.classList.remove('drag-over')); });
    card.addEventListener('dragover', e => { if (!dragId) return; e.preventDefault(); card.classList.add('drag-over'); });
    card.addEventListener('dragleave', () => card.classList.remove('drag-over'));
    card.addEventListener('drop', e => { e.preventDefault(); if (!dragId || dragId === id) return; const from = state.trip.stops.findIndex(s => s.id === dragId), to = state.trip.stops.findIndex(s => s.id === id); const [stop] = state.trip.stops.splice(from,1); state.trip.stops.splice(to,0,stop); dragId = null; dirty(); renderStops(); renderMap(); });
  });
}
function focusStop(id) {
  state.selected = id; renderStops(); renderMap();
  const s = state.trip.stops.find(s => s.id === id);
  if (map && s) map.setView([s.latitude,s.longitude], 10);
}
function stopAction(id, action) {
  const index = state.trip.stops.findIndex(s => s.id === id), stop = state.trip.stops[index];
  if (action === 'camp') { openCampsites(id); return; }
  if (action === 'edit') { openPlaceDialog(id); return; }
  if (action === 'remove') { if (!confirm(`Remove ${stop.name} from this trip?`)) return; state.trip.stops.splice(index,1); if (state.selected === id) state.selected = state.trip.stops[0]?.id; }
  if (action === 'up' && index > 0) [state.trip.stops[index-1],state.trip.stops[index]] = [stop,state.trip.stops[index-1]];
  if (action === 'down' && index < state.trip.stops.length-1) [state.trip.stops[index+1],state.trip.stops[index]] = [stop,state.trip.stops[index+1]];
  if (action === 'clear-camp') stop.campsite = null;
  dirty(); renderStops(); renderMap();
}
function openPlaceDialog(id = '', target = null) {
  if (state.mapInsert) { state.mapInsert = null; renderMap(); }
  state.placeTarget = target;
  $('place-dialog').dataset.editStop = id;
  $('place-dialog-title').textContent = target?.endpoint === 'start' ? 'Where does your trip start?' : target?.endpoint === 'end' ? 'Where does your trip finish?' : target?.insertIndex != null ? 'A place along the way' : id ? 'Choose a new location' : 'Where do you want to go?';
  $('place-results').innerHTML = '';
  const endpoint = target?.endpoint ? state.trip[`${target.endpoint}Location`] : null;
  $('place-query').value = id ? state.trip.stops.find(s => s.id === id).name : endpoint?.name ?? '';
  $('place-dialog').showModal(); $('place-query').focus();
}
function addPlace(place) {
  const editing = $('place-dialog').dataset.editStop, target = state.placeTarget;
  if (target?.endpoint) {
    state.trip[`${target.endpoint}Location`] = {name:place.name,description:place.description ?? place.name,latitude:place.latitude,longitude:place.longitude};
    renderEndpoints();
  } else {
    if (!editing && state.trip.stops.length >= 100) { notify('A trip can have up to 100 stops.', true); return; }
    const stop = {id:uid(),name:place.name,latitude:place.latitude,longitude:place.longitude,nights:target?.insertIndex != null ? 0 : 1,notes:'',campsite:null};
    if (editing) {
      const index = state.trip.stops.findIndex(s => s.id === editing);
      if (index < 0) return;
      state.trip.stops[index] = {...state.trip.stops[index],name:place.name,latitude:place.latitude,longitude:place.longitude,campsite:null};
      state.selected = editing;
    } else {
      state.trip.stops.splice(target?.insertIndex ?? state.trip.stops.length, 0, stop); state.selected = stop.id;
    }
  }
  state.camps = []; dirty(); renderStops(); renderMap(true);
  if ($('place-dialog').open) $('place-dialog').close();
  notify(`${place.name} ${editing ? 'updated' : 'added'}. Your road distances will update.`);
}
function openInsert(index) {
  state.insertLeg = index;
  const nodes = routeNodes(state.trip), leg = validRoute()?.legs[index];
  $('insert-description').textContent = `${nodes[index].name} → ${nodes[index+1].name}${leg ? ` · ${Math.round(leg.distanceKm)} km · ${formatDriving(leg.durationHours)}` : ''}`;
  $('insert-midpoint').disabled = !leg;
  $('insert-midpoint').title = leg ? 'Add a point halfway along this driving route' : 'Update the driving route first to find the halfway point';
  $('insert-dialog').showModal();
}
function insertHalfway() {
  const leg = validRoute()?.legs[state.insertLeg]; if (!leg) return;
  const index = insertionIndex(state.trip,state.insertLeg);
  state.placeTarget = {insertIndex:index}; $('place-dialog').dataset.editStop = '';
  $('insert-dialog').close();
  addPlace({name:'Halfway stop',latitude:leg.midpoint.latitude,longitude:leg.midpoint.longitude});
  notify('Halfway stop added on the road. Refine its location or find a camping nearby.');
}
function scheduleRoute() {
  const key = pointKey();
  if (key === state.routeKey || key === queuedRouteKey) return;
  clearTimeout(routeTimer); routeController?.abort(); routeRequest++;
  state.routing = false; state.routeError = null; queuedRouteKey = key;
  if (routeNodes(state.trip).length < 2) { queuedRouteKey = null; renderSummary(); return; }
  state.routing = true; renderSummary(); renderLegs();
  routeTimer = setTimeout(() => calculateRoute(false),700);
}
function openTripDialog(edit = false) {
  if (state.busy) return;
  $('trip-form').dataset.mode = edit ? 'edit' : 'new';
  $('trip-dialog-title').textContent = edit ? 'Your trip, your way' : 'A new adventure';
  $('trip-name').value = edit ? state.trip.name : ''; $('trip-date').value = edit ? state.trip.startDate ?? '' : '';
  for (const id of ['delete-trip','duplicate-trip','export-trip']) $(id).hidden = !edit;
  $('trip-dialog').showModal();
}
async function saveTrip() {
  if (!state.trip || !state.dirty) return;
  await busy(async () => {
    const saved = await api(`/api/trips/${state.trip.id}`, {method:'PUT',body:JSON.stringify(state.trip)});
    state.trip = saved; state.trips = state.trips.map(t => t.id === saved.id ? saved : t); state.dirty = false; renderSidebar(); notify('Trip saved. The road can wait.');
  });
}
function renderMap(fit = false) {
  if (!map) return;
  stopLayer.clearLayers(); campLayer.clearLayers(); lineLayer.clearLayers();
  $('map-pick-banner').hidden = !state.mapInsert;
  $('map').classList.toggle('picking', !!state.mapInsert);
  if (!state.trip) return;
  const nodes = routeNodes(state.trip), points = nodes.map(s => [s.latitude,s.longitude]);
  nodes.forEach(s => {
    const endpoint = s.kind !== 'stop';
    const marker = L.marker([s.latitude,s.longitude],{icon:L.divIcon({className:`map-stop ${endpoint ? `map-${s.kind}` : ''} ${s.key === state.selected ? 'is-selected' : ''}`,html:endpoint ? (s.kind === 'start' ? 'S' : 'F') : String(s.stopIndex+1),iconSize:[32,32],iconAnchor:[16,16]})}).addTo(stopLayer);
    marker.bindTooltip(esc(`${endpoint ? s.kind === 'start' ? 'Start: ' : 'Finish: ' : ''}${s.name}`));
    marker.on('click', () => { if (state.mapInsert) return; if (endpoint) openPlaceDialog('',{endpoint:s.kind}); else focusStop(s.key); });
    if (s.campsite) addCampMarker(s.campsite,true);
  });
  const route = validRoute();
  if (route) route.legs.forEach((leg,i) => L.polyline(leg.coordinates,{color:i%2 ? '#007db3' : '#00aeb5',weight:5,opacity:.85}).bindTooltip(`${esc(nodes[i].name)} → ${esc(nodes[i+1].name)} · ${Math.round(leg.distanceKm)} km`).on('click', () => { if (!state.mapInsert) openInsert(i); }).addTo(lineLayer));
  else if (points.length > 1) L.polyline(points,{color:'#00aeb5',weight:3,dashArray:'5,8',opacity:.55}).addTo(lineLayer);
  for (const camp of state.camps) addCampMarker(camp,false);
  if (fit && points.length) { map.invalidateSize(); map.fitBounds(points,{padding:[45,45],maxZoom:11}); }
}
function addCampMarker(camp, selected) {
  L.marker([camp.latitude,camp.longitude],{icon:L.divIcon({className:'map-camp',iconSize:[14,14],iconAnchor:[7,7]})})
    .bindPopup(`<strong>${esc(camp.name)}</strong><br>${selected ? 'Saved campsite' : `${camp.distanceKm} km from stop`}`).addTo(campLayer);
}
async function openCampsites(id) {
  state.selected = id; state.campStop = id; state.camps = []; state.campWarnings = []; renderStops(); renderMap();
  const stop = state.trip.stops.find(s => s.id === id);
  $('camp-title').textContent = `Campings near ${stop.name}`; $('camp-name-filter').value = '';
  $('camp-search-links').innerHTML = `<span>Also explore:</span><a href="https://www.google.com/maps/search/camping+near+${stop.latitude},${stop.longitude}" target="_blank" rel="noopener noreferrer">Google Maps ↗</a><a href="https://www.openstreetmap.org/#map=11/${stop.latitude}/${stop.longitude}" target="_blank" rel="noopener noreferrer">OpenStreetMap ↗</a>`;
  $('camp-dialog').showModal();
  await findCampsites();
}
function lockCampSearch(locked) {
  state.campLoading = locked;
  $('camp-form').querySelector('button').disabled = locked;
  for (const id of ['camp-radius','camp-type','camp-source','camp-name-filter']) $(id).disabled = locked;
}
async function findCampsites() {
  const stop = state.trip.stops.find(s => s.id === state.campStop); if (!stop) return;
  state.camps = []; state.campWarnings = []; state.campError = null; renderMap();
  const token = ++campRequest, radius = Number($('camp-radius').value);
  $('camp-results').innerHTML = '<div class="status-message">Searching OpenStreetMap and Park4Night… Busy providers may take up to a minute.</div>';
  lockCampSearch(true);
  try {
    const result = await api(`/api/campsites/search?latitude=${stop.latitude}&longitude=${stop.longitude}&radiusKm=${radius}`);
    if (token !== campRequest || !state.trip.stops.some(s => s.id === stop.id)) return;
    state.camps = result.campsites; state.campWarnings = result.warnings; renderCampsites(); renderMap();
  } catch (e) { if (token === campRequest) { state.campError = e.message; renderCampsites(); } }
  finally { if (token === campRequest) lockCampSearch(false); }
}
function renderCampsites() {
  if (state.campError) { $('camp-results').innerHTML = `<div class="status-message error">${esc(state.campError)}</div>`; return; }
  const type = $('camp-type').value, source = $('camp-source').value, query = $('camp-name-filter').value;
  const camps = state.camps.filter(c => matchesCampsite(c,type,source,query));
  const selected = state.trip.stops.find(s => s.id === state.campStop)?.campsite?.id;
  const warnings = state.campWarnings.map(w => `<div class="status-message provider-warning">${esc(w)}</div>`).join('');
  const cards = camps.map(c => {
    const sources = campsiteSources(c);
    const rating = Number.isFinite(c.rating) ? `<span class="camp-rating">★ ${c.rating.toFixed(1)}/5${Number.isInteger(c.reviewCount) ? ` · ${c.reviewCount} reviews` : ''}</span>` : '';
    return `<article class="camp-card"><div class="camp-card-head"><h3>${esc(c.name)}</h3><span class="camp-distance">${c.distanceKm} km</span></div><div class="camp-source-row">${sources.map(s => `<span class="source-badge ${s.name === 'Park4Night' ? 'source-p4n' : ''}">${esc(s.name)}</span>`).join('')}${rating}</div><p class="camp-type">${esc(campsiteType(c))}${c.access === 'private' ? ' · Private access: verify that visitors are accepted' : c.access === 'customers' ? ' · For guests' : ''}</p><div class="facilities">${c.facilities.length ? c.facilities.map(f => `<span class="facility">${esc(f)}</span>`).join('') : '<span class="muted">Facilities not listed</span>'}</div><div class="camp-card-footer">${safeUrl(c.website) ? `<a href="${esc(safeUrl(c.website))}" target="_blank" rel="noopener noreferrer">Visit website ↗</a>` : ''}${sources.map(s => `<a href="${esc(s.url)}" target="_blank" rel="noopener noreferrer">${esc(s.name)} ↗</a>`).join('')}<button class="primary" data-choose="${esc(c.id)}" ${c.id === selected ? 'disabled' : ''}>${c.id === selected ? 'Selected ✓' : 'Stay here'}</button></div></article>`;
  }).join('');
  $('camp-results').innerHTML = warnings + (camps.length ? `<p class="muted">${camps.length} ${camps.length === 1 ? 'place' : 'places'} to stay · nearest first</p>${cards}` : '<div class="status-message">No campsites found for this filter. Try a larger radius, another source, or include other overnight spots. Use the map-search links above for more listings.</div>');
  $('camp-results').querySelectorAll('[data-choose]').forEach(b => b.addEventListener('click', () => {
    const stop = state.trip.stops.find(s => s.id === state.campStop); if (!stop) return;
    stop.campsite = clone(state.camps.find(c => c.id === b.dataset.choose)); if (!stop.nights) stop.nights = 1; dirty(); renderStops(); renderMap(); renderCampsites();
    notify('Campsite added to this stop. Remember to save your trip.');
  }));
}
async function calculateRoute(manual = true) {
  clearTimeout(routeTimer);
  const nodes = routeNodes(state.trip); if (nodes.length < 2) return;
  routeController?.abort(); routeController = new AbortController();
  const token = ++routeRequest, key = pointKey(), tripId = state.trip.id;
  queuedRouteKey = key; state.routing = true; state.routeError = null; renderSummary(); renderLegs();
  try {
    const result = await api('/api/route',{method:'POST',signal:routeController.signal,body:JSON.stringify({points:nodes.map(s => ({latitude:s.latitude,longitude:s.longitude}))})});
    if (token !== routeRequest || state.trip?.id !== tripId || pointKey() !== key) return;
    state.route = result; state.routeKey = key; renderMap(); renderLegs();
  } catch (e) {
    if (e.name === 'AbortError' || token !== routeRequest) return;
    state.routeError = e.message;
    if (manual) notify(e.message,true);
  } finally {
    if (token === routeRequest) { state.routing = false; queuedRouteKey = null; renderSummary(); renderLegs(); }
  }
}
function exportTrip() {
  const blob = new Blob([JSON.stringify(state.trip,null,2)],{type:'application/json'}), url = URL.createObjectURL(blob), a = document.createElement('a');
  a.href = url; a.download = `${state.trip.name.replace(/[^a-z0-9_-]/gi,'-') || 'roadtrip'}.json`; a.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
}

$('new-trip').addEventListener('click', () => openTripDialog());
$('first-trip').addEventListener('click', () => openTripDialog());
$('trip-settings').addEventListener('click', () => openTripDialog(true));
$('save-trip').addEventListener('click', saveTrip);
$('trip-notes').addEventListener('input', e => { state.trip.notes = e.target.value; dirty(); });
for (const id of ['add-place','add-place-bottom']) $(id).addEventListener('click', () => openPlaceDialog());
for (const b of document.querySelectorAll('[data-close]')) b.addEventListener('click', () => $(b.dataset.close).close());
$('fit-map').addEventListener('click', () => renderMap(true));
$('calculate-route').addEventListener('click', () => calculateRoute());
$('camp-form').addEventListener('submit', e => { e.preventDefault(); findCampsites(); });
$('camp-type').addEventListener('change', renderCampsites);
$('camp-source').addEventListener('change', renderCampsites);
$('camp-name-filter').addEventListener('input', renderCampsites);
$('set-start').addEventListener('click', () => openPlaceDialog('',{endpoint:'start'}));
$('set-end').addEventListener('click', () => openPlaceDialog('',{endpoint:'end'}));
$('return-home').addEventListener('click', () => { if (!state.trip.startLocation) return; state.trip.endLocation = clone(state.trip.startLocation); dirty(); renderEndpoints(); renderStops(); renderMap(true); });
$('clear-endpoints').addEventListener('click', () => { state.trip.startLocation = null; state.trip.endLocation = null; dirty(); renderEndpoints(); renderStops(); renderMap(true); });
$('insert-midpoint').addEventListener('click', insertHalfway);
$('insert-search').addEventListener('click', () => { const index = insertionIndex(state.trip,state.insertLeg); $('insert-dialog').close(); openPlaceDialog('',{insertIndex:index}); });
$('insert-map').addEventListener('click', () => { state.mapInsert = {index:insertionIndex(state.trip,state.insertLeg)}; $('insert-dialog').close(); renderMap(); $('map').scrollIntoView({behavior:'smooth',block:'center'}); });
$('cancel-map-pick').addEventListener('click', () => { state.mapInsert = null; renderMap(); });
$('export-trip').addEventListener('click', exportTrip);
$('trip-form').addEventListener('submit', async e => {
  e.preventDefault();
  if ($('trip-form').dataset.mode === 'edit') {
    state.trip.name = $('trip-name').value.trim(); state.trip.startDate = $('trip-date').value || null;
    dirty(); render(); $('trip-dialog').close(); await saveTrip();
  } else {
    if (!canLeave()) return;
    await busy(async () => {
      const trip = await api('/api/trips',{method:'POST',body:JSON.stringify({name:$('trip-name').value.trim(),startDate:$('trip-date').value || null,notes:'',stops:[]})});
      state.trips.unshift(trip); setTrip(trip); $('trip-dialog').close(); notify('A new adventure, ready to plan.');
    });
  }
});
$('delete-trip').addEventListener('click', async () => {
  if (!confirm(`Delete “${state.trip.name}” and all its stops? This cannot be undone.`)) return;
  await busy(async () => {
    await api(`/api/trips/${state.trip.id}?version=${state.trip.version}`,{method:'DELETE'});
    state.trips = state.trips.filter(t => t.id !== state.trip.id); setTrip(state.trips[0] ?? null); $('trip-dialog').close(); notify('Trip deleted.');
  });
});
$('duplicate-trip').addEventListener('click', async () => {
  await busy(async () => {
    const trip = await api('/api/trips',{method:'POST',body:JSON.stringify({...state.trip,name:`${state.trip.name.slice(0,113)} (copy)`})});
    state.trips.unshift(trip); setTrip(trip); $('trip-dialog').close(); notify('Trip duplicated. Make this one your own.');
  });
});
$('place-form').addEventListener('submit', async e => {
  e.preventDefault(); const token = ++placeRequest, button = e.target.querySelector('button'); button.disabled = true;
  $('place-results').innerHTML = '<div class="status-message">Looking for your next stop…</div>';
  try {
    const places = await api(`/api/places?q=${encodeURIComponent($('place-query').value.trim())}`); if (token !== placeRequest) return;
    $('place-results').innerHTML = places.length ? places.map((p,i) => `<button class="search-result" data-place="${i}"><span><strong>${esc(p.name)}</strong><small>${esc(p.description)}</small></span><span>+</span></button>`).join('') : '<div class="status-message">No places found. Try adding a region or country to your search, or use coordinates below.</div>';
    $('place-results').querySelectorAll('[data-place]').forEach(b => b.addEventListener('click', () => addPlace(places[Number(b.dataset.place)])));
  } catch (error) { if (token === placeRequest) $('place-results').innerHTML = `<div class="status-message error">${esc(error.message)}</div>`; }
  finally { if (token === placeRequest) button.disabled = false; }
});
$('manual-form').addEventListener('submit', e => { e.preventDefault(); addPlace({name:$('manual-name').value.trim(),latitude:Number($('manual-lat').value),longitude:Number($('manual-lon').value)}); e.target.reset(); });
$('import-trip').addEventListener('click', () => { if (!state.busy) $('import-file').click(); });
$('import-file').addEventListener('change', async e => {
  const file = e.target.files[0]; if (!file) return;
  try {
    if (file.size > 1_000_000) throw new Error('Please use a trip JSON file smaller than 1 MB.');
    const input = JSON.parse(await file.text()); if (!canLeave()) return;
    await busy(async () => { const trip = await api('/api/trips',{method:'POST',body:JSON.stringify(input)}); state.trips.unshift(trip); setTrip(trip); notify('Trip imported as a new trip.'); });
  } catch (error) { notify(error.message, true); }
  finally { e.target.value = ''; }
});
window.addEventListener('beforeunload', e => { if (state.dirty) { e.preventDefault(); e.returnValue = ''; } });
window.addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); saveTrip(); } });
function initMap() {
  if (!globalThis.L) { $('map-fallback').hidden = false; return; }
  map = L.map('map',{scrollWheelZoom:false}).setView([47,6],5);
  map.on('click', e => { if (!state.mapInsert) return; const index = state.mapInsert.index; state.mapInsert = null; state.placeTarget = {insertIndex:index}; $('place-dialog').dataset.editStop = ''; addPlace({name:'Travel stop',latitude:e.latlng.lat,longitude:e.latlng.lng}); });
  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'}).addTo(map);
  stopLayer = L.layerGroup().addTo(map); lineLayer = L.layerGroup().addTo(map); campLayer = L.layerGroup().addTo(map);
}
async function init() {
  initMap();
  try {
    state.trips = await api('/api/trips'); let preferred;
    try { preferred = localStorage.getItem('roam-trip'); } catch { /* Optional. */ }
    setTrip(state.trips.find(t => t.id === preferred) ?? state.trips[0] ?? null);
  } catch (error) { $('welcome').hidden = false; notify(`Could not load your trips. ${error.message} Reload the page to try again.`,true); }
}
if (document.readyState === 'complete') init();
else window.addEventListener('load', init, {once:true});
