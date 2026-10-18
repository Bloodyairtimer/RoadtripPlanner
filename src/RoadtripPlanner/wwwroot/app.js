const $ = id => document.getElementById(id);
const state = { trips: [], trip: null, selected: null, dirty: false, busy: false, camps: [], campStop: null, route: null, routeKey: null };
let map, stopLayer, campLayer, lineLayer, toastTimer, dragId, placeRequest = 0, campRequest = 0;
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const clone = value => structuredClone(value);
const uid = () => globalThis.crypto?.randomUUID?.() ?? 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.floor(Math.random()*16); return (c === 'x' ? r : (r & 3 | 8)).toString(16); });
const safeUrl = value => { try { const u = new URL(value); return ['https:', 'http:'].includes(u.protocol) ? u.href : null; } catch { return null; } };
const dateAt = (date, days) => { const d = new Date(`${date}T12:00:00Z`); d.setUTCDate(d.getUTCDate() + days); return d; };
const dateLabel = date => new Intl.DateTimeFormat(undefined, {day:'numeric',month:'short',year:'numeric',timeZone:'UTC'}).format(date);
const pointKey = () => JSON.stringify(state.trip?.stops.map(s => [s.latitude, s.longitude]) ?? []);

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
function dirty() { state.dirty = true; renderSave(); renderSummary(); }
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
  state.trip = trip ? clone(trip) : null; state.dirty = false; state.route = null; state.routeKey = null; state.camps = []; state.selected = trip?.stops[0]?.id ?? null;
  render(); renderMap(true);
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
  if (trip) { $('trip-notes').value = trip.notes; renderStops(); renderSummary(); }
  renderSidebar(); renderSave();
  if (map) requestAnimationFrame(() => map.invalidateSize());
}
function renderSummary() {
  if (!state.trip) return;
  const t = state.trip, nights = t.stops.reduce((n,s) => n + Number(s.nights || 0), 0);
  $('summary-stops').textContent = t.stops.length; $('summary-nights').textContent = nights;
  $('summary-date').textContent = t.startDate ? dateLabel(dateAt(t.startDate, 0)) : 'No date set';
  $('summary-end').textContent = t.startDate ? `Until ${dateLabel(dateAt(t.startDate, nights))}` : "Go whenever you're ready";
  $('calculate-route').disabled = t.stops.length < 2;
  const validRoute = state.route && state.routeKey === pointKey();
  $('summary-distance').textContent = validRoute ? `${Math.round(state.route.distanceKm).toLocaleString()} km` : t.stops.length < 2 ? 'Your route awaits' : 'Ready to calculate';
  $('summary-duration').textContent = validRoute ? `${state.route.durationHours.toFixed(1)} hours driving` : t.stops.length < 2 ? 'Add a couple of stops' : 'Use “Calculate driving route”';
  $('route-note').textContent = validRoute ? 'Car routing estimate, excluding breaks and traffic. Check vehicle height, weight and road restrictions for your camper.' : 'Dotted lines connect your stops. Calculate a road route for driving estimates.';
}
function renderStops() {
  let days = 0;
  $('stop-list').innerHTML = state.trip.stops.length ? state.trip.stops.map((s,i) => {
    const arrival = state.trip.startDate ? dateAt(state.trip.startDate, days) : null;
    days += s.nights;
    return `<article class="stop-card ${s.id === state.selected ? 'selected' : ''}" data-stop="${s.id}"><span class="stop-number">${i+1}</span><div class="stop-top"><div><h3 tabindex="0" data-focus-stop="${s.id}">${esc(s.name)}</h3><div class="stop-date">${arrival ? esc(dateLabel(arrival)) : 'Date to be decided'} · ${s.nights === 0 ? 'Day visit' : `${s.nights} night${s.nights === 1 ? '' : 's'}`}</div></div><div class="reorder"><span class="drag-handle" draggable="true" aria-hidden="true" title="Drag to reorder">⠿</span><button data-action="edit" aria-label="Change ${esc(s.name)} location" title="Change location">✎</button><button data-action="up" aria-label="Move ${esc(s.name)} earlier" ${i === 0 ? 'disabled' : ''}>↑</button><button data-action="down" aria-label="Move ${esc(s.name)} later" ${i === state.trip.stops.length-1 ? 'disabled' : ''}>↓</button><button data-action="remove" aria-label="Remove ${esc(s.name)}">×</button></div></div><div class="stop-fields"><label class="nights-control" style="margin:0">☾ <input type="number" min="0" max="365" value="${s.nights}" data-field="nights" aria-label="Nights in ${esc(s.name)}"> nights</label><button class="camp-button" data-action="camp">♧ ${s.campsite ? 'Change campsite' : 'Find campsites'}</button></div>${s.campsite ? `<div class="selected-camp"><div><strong>♧ ${esc(s.campsite.name)}</strong><small>${s.campsite.distanceKm} km from stop · ${s.campsite.type === 'camp_site' ? 'Camping' : 'Camper / caravan site'}</small></div><button data-action="clear-camp" aria-label="Remove campsite">×</button></div>` : ''}<textarea class="stop-notes" rows="1" maxlength="5000" data-field="notes" aria-label="Notes for ${esc(s.name)}" placeholder="A little note for this stop…">${esc(s.notes)}</textarea></article>`;
  }).join('') : '<div class="empty-stops">The road is wide open.<br>Add your first place and start connecting the dots.</div>';
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
function openPlaceDialog(id = '') {
  $('place-dialog').dataset.editStop = id;
  $('place-dialog-title').textContent = id ? 'Choose a new location' : 'Where do you want to go?';
  $('place-results').innerHTML = '';
  $('place-query').value = id ? state.trip.stops.find(s => s.id === id).name : '';
  $('place-dialog').showModal(); $('place-query').focus();
}
function addPlace(place) {
  const editing = $('place-dialog').dataset.editStop;
  if (!editing && state.trip.stops.length >= 100) { notify('A trip can have up to 100 stops.', true); return; }
  const stop = { id:uid(), name:place.name, latitude:place.latitude, longitude:place.longitude, nights:1, notes:'', campsite:null };
  if (editing) {
    const index = state.trip.stops.findIndex(s => s.id === editing);
    if (index < 0) return;
    state.trip.stops[index] = {...state.trip.stops[index],name:place.name,latitude:place.latitude,longitude:place.longitude,campsite:null};
    state.selected = editing;
  } else { state.trip.stops.push(stop); state.selected = stop.id; }
  state.camps = []; dirty(); renderStops(); renderMap(true); $('place-dialog').close();
  notify(`${place.name} ${editing ? 'updated' : 'added'}. Save your changes when you're ready.`);
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
  if (!state.trip) return;
  const points = [];
  state.trip.stops.forEach((s,i) => {
    const point = [s.latitude,s.longitude]; points.push(point);
    const marker = L.marker(point,{icon:L.divIcon({className:`map-stop ${s.id === state.selected ? 'is-selected' : ''}`,html:String(i+1),iconSize:[30,30],iconAnchor:[15,15]})}).addTo(stopLayer);
    marker.bindTooltip(esc(s.name)); marker.on('click', () => { state.selected = s.id; renderStops(); openCampsites(s.id); });
    if (s.campsite) addCampMarker(s.campsite, true);
  });
  if (state.route && state.routeKey === pointKey()) L.polyline(state.route.coordinates,{color:'#325f4d',weight:4,opacity:.8}).addTo(lineLayer);
  else if (points.length > 1) L.polyline(points,{color:'#6f8972',weight:2,dashArray:'5,8',opacity:.7}).addTo(lineLayer);
  for (const camp of state.camps) addCampMarker(camp, false);
  if (fit && points.length) { map.invalidateSize(); map.fitBounds(points,{padding:[50,50],maxZoom:11}); }
}
function addCampMarker(camp, selected) {
  L.marker([camp.latitude,camp.longitude],{icon:L.divIcon({className:'map-camp',iconSize:[14,14],iconAnchor:[7,7]})})
    .bindPopup(`<strong>${esc(camp.name)}</strong><br>${selected ? 'Saved campsite' : `${camp.distanceKm} km from stop`}`).addTo(campLayer);
}
async function openCampsites(id) {
  state.selected = id; state.campStop = id; state.camps = []; renderStops(); renderMap();
  const stop = state.trip.stops.find(s => s.id === id);
  $('camp-title').textContent = `A night near ${stop.name}`; $('camp-dialog').showModal();
  await findCampsites();
}
async function findCampsites() {
  const stop = state.trip.stops.find(s => s.id === state.campStop); if (!stop) return;
  const token = ++campRequest, radius = Number($('camp-radius').value);
  $('camp-results').innerHTML = '<div class="status-message">Exploring the area for campsites…</div>';
  $('camp-form').querySelector('button').disabled = true;
  try {
    const camps = await api(`/api/campsites?latitude=${stop.latitude}&longitude=${stop.longitude}&radiusKm=${radius}`);
    if (token !== campRequest || !state.trip.stops.some(s => s.id === stop.id)) return;
    state.camps = camps; renderCampsites(); renderMap();
  } catch (e) { if (token === campRequest) $('camp-results').innerHTML = `<div class="status-message error">${esc(e.message)}</div>`; }
  finally { if (token === campRequest) $('camp-form').querySelector('button').disabled = false; }
}
function renderCampsites() {
  const type = $('camp-type').value;
  const camps = state.camps.filter(c => type === 'all' || c.type === type);
  const selected = state.trip.stops.find(s => s.id === state.campStop)?.campsite?.id;
  $('camp-results').innerHTML = camps.length ? `<p class="muted">${camps.length} places to stay · nearest first</p>` + camps.map(c => `<article class="camp-card"><div class="camp-card-head"><h3>${esc(c.name)}</h3><span class="camp-distance">${c.distanceKm} km</span></div><p class="camp-type">${c.type === 'camp_site' ? 'Camping' : 'Camper / caravan site'}</p><div class="facilities">${c.facilities.length ? c.facilities.map(f => `<span class="facility">${esc(f)}</span>`).join('') : '<span class="muted">Facilities not listed</span>'}</div><div class="camp-card-footer">${safeUrl(c.website) ? `<a href="${esc(safeUrl(c.website))}" target="_blank" rel="noopener noreferrer">Visit website ↗</a>` : ''}<a href="https://www.openstreetmap.org/${esc(c.id)}" target="_blank" rel="noopener noreferrer">On the map ↗</a><button class="primary" data-choose="${esc(c.id)}" ${c.id === selected ? 'disabled' : ''}>${c.id === selected ? 'Selected ✓' : 'Stay here'}</button></div></article>`).join('') : '<div class="status-message">No campsites found for this filter. Try a larger radius or include camper / caravan sites. You can also search a nearby town.</div>';
  $('camp-results').querySelectorAll('[data-choose]').forEach(b => b.addEventListener('click', () => {
    const stop = state.trip.stops.find(s => s.id === state.campStop); if (!stop) return;
    stop.campsite = clone(state.camps.find(c => c.id === b.dataset.choose)); dirty(); renderStops(); renderMap(); renderCampsites();
    notify('Campsite added to this stop. Remember to save your trip.');
  }));
}
async function calculateRoute() {
  if (state.trip.stops.length < 2) return;
  const key = pointKey(), tripId = state.trip.id, button = $('calculate-route');
  button.disabled = true; button.textContent = 'Calculating…';
  try {
    const result = await api('/api/route',{method:'POST',body:JSON.stringify({points:state.trip.stops.map(s => ({latitude:s.latitude,longitude:s.longitude}))})});
    if (state.trip?.id !== tripId || pointKey() !== key) return;
    state.route = result; state.routeKey = key; renderMap(); renderSummary();
  } catch (e) { notify(e.message, true); }
  finally { button.textContent = 'Calculate driving route'; button.disabled = (state.trip?.stops.length ?? 0) < 2; }
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
$('calculate-route').addEventListener('click', calculateRoute);
$('camp-form').addEventListener('submit', e => { e.preventDefault(); findCampsites(); });
$('camp-type').addEventListener('change', renderCampsites);
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
    $('place-results').innerHTML = places.length ? places.map((p,i) => `<button class="search-result" data-place="${i}"><span><strong>${esc(p.name)}</strong><small>${esc(p.description)}</small></span><span>＋</span></button>`).join('') : '<div class="status-message">No places found. Try adding a region or country to your search, or use coordinates below.</div>';
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
