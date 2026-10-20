const {test,expect} = require('playwright/test');
const places = {
  Ermelo:{name:'Ermelo',description:'Ermelo, Netherlands',latitude:52.3,longitude:5.6},
  Nice:{name:'Nice',description:'Nice, France',latitude:43.7,longitude:7.27},
  Milan:{name:'Milan',description:'Milan, Italy',latitude:45.46,longitude:9.19},
  Annecy:{name:'Annecy',description:'Annecy, France',latitude:45.9,longitude:6.13}
};
async function fixtures(page) {
  let routeCalls = 0;
  await page.route('https://tile.openstreetmap.org/**',r => r.abort());
  await page.route('**/api/places?*',r => r.fulfill({json:[places[new URL(r.request().url()).searchParams.get('q')]].filter(Boolean)}));
  await page.route('**/api/campsites/search?*',r => {
    const q=new URL(r.request().url()).searchParams;
    return r.fulfill({json:{campsites:[{id:'way/123',name:'Sunny Coast Camping',latitude:Number(q.get('latitude'))+.01,longitude:Number(q.get('longitude'))+.01,distanceKm:1.4,type:'camp_site',website:'https://example.com',phone:null,facilities:['Showers','Water'],access:'customers'}],warnings:[]}});
  });
  await page.route('**/api/route',r => {
    routeCalls++;
    const {points}=r.request().postDataJSON();
    const legs=points.slice(1).map((p,i) => ({distanceKm:100,durationHours:1.5,coordinates:[[points[i].latitude,points[i].longitude],[p.latitude,p.longitude]],midpoint:{latitude:(points[i].latitude+p.latitude)/2,longitude:(points[i].longitude+p.longitude)/2}}));
    return r.fulfill({json:{distanceKm:100*legs.length,durationHours:1.5*legs.length,coordinates:points.map(p => [p.latitude,p.longitude]),legs}});
  });
  return () => routeCalls;
}
async function choosePlace(page,name) {
  await page.getByRole('textbox',{name:'Search for a place'}).fill(name);
  await page.locator('#place-form').getByRole('button',{name:'Search',exact:true}).click();
  await page.locator('.search-result').filter({hasText:name}).click();
}
async function newTrip(page) {
  await page.goto('/');
  await page.locator('#new-trip').click();
  await page.locator('#trip-name').fill(`Summer coast ${Date.now()}`);
  await page.locator('#trip-date').fill('2027-04-24');
  await page.locator('#trip-end-date').fill('2027-05-08');
  await page.locator('#trip-form').getByRole('button',{name:'Save trip',exact:true}).click();
  await expect(page.locator('#workspace')).toBeVisible();
}
test('plan endpoints, insert stops, select camping, save and reload',async ({page}) => {
  const errors=[];page.on('pageerror',e => errors.push(e.message));
  const calls=await fixtures(page);await newTrip(page);
  await page.locator('#set-start').click();await choosePlace(page,'Ermelo');
  await page.locator('#set-end').click();await choosePlace(page,'Nice');
  await expect(page.locator('#summary-distance')).toHaveText('100 km');
  await page.locator('#add-place').click();await choosePlace(page,'Milan');
  await expect(page.locator('#summary-distance')).toHaveText('200 km');
  await expect(page.locator('.drive-leg')).toHaveCount(2);
  await page.locator('[data-insert-leg="0"]').click();
  await page.locator('#insert-midpoint').click();
  await expect(page.locator('.stop-card')).toHaveCount(2);
  await expect(page.locator('.stop-card h3').first()).toHaveText('Halfway stop');
  await expect(page.locator('#summary-distance')).toHaveText('300 km');
  const beforeNotes=calls();
  await page.locator('.stop-notes').first().fill('Find a camping for the night');
  await page.waitForTimeout(900);expect(calls()).toBe(beforeNotes);
  await page.locator('.stop-card').first().getByRole('button',{name:'Find campsites'}).click();
  await expect(page.locator('#camp-results')).toContainText('Sunny Coast Camping');
  await page.getByRole('button',{name:'Stay here',exact:true}).click();
  await page.locator('[data-close="camp-dialog"]').click();
  await expect(page.locator('.nights-control input').first()).toHaveValue('1');
  await page.locator('#save-trip').click();await expect(page.locator('#save-state')).toHaveText('All changes saved');
  await page.reload();
  await expect(page.locator('#start-label')).toHaveText('Ermelo');
  await expect(page.locator('#end-label')).toHaveText('Nice');
  await expect(page.locator('.selected-camp')).toContainText('Sunny Coast Camping');
  await expect(page.locator('.stop-notes').first()).toHaveValue('Find a camping for the night');
  await expect(page.locator('#summary-distance')).toHaveText('300 km');
  await page.screenshot({path:'../../TestResults/planner-desktop.png',fullPage:true});
  await page.locator('[data-insert-leg="2"]').click();await page.locator('#insert-search').click();await choosePlace(page,'Annecy');
  await expect(page.locator('.stop-card h3').last()).toHaveText('Annecy');
  await expect(page.locator('#summary-distance')).toHaveText('400 km');
  await page.locator('[data-insert-leg="0"]').click();await page.locator('#insert-map').click();
  await expect(page.locator('#map-pick-banner')).toBeVisible();
  await page.locator('#map').click({position:{x:140,y:140}});
  await expect(page.locator('.stop-card h3').first()).toHaveText('Travel stop');
  await expect(page.locator('#map-pick-banner')).toBeHidden();
  await expect(page.locator('#summary-distance')).toHaveText('500 km');
  await expect(page.locator('#toast')).toBeHidden({timeout:6000});
  await page.setViewportSize({width:390,height:844});
  await page.screenshot({path:'../../TestResults/planner-mobile.png',fullPage:true});
  expect(await page.evaluate(() => document.documentElement.scrollWidth<=window.innerWidth)).toBe(true);
  expect(errors).toEqual([]);
});
test('campsite errors stay distinct from an empty successful search',async ({page}) => {
  await fixtures(page);await newTrip(page);
  await page.locator('#add-place').click();await choosePlace(page,'Nice');
  await page.route('**/api/campsites/search?*',r => r.fulfill({status:503,json:{detail:'Campsite providers are busy. Try again.'}}));
  await page.locator('.camp-button').click();
  await expect(page.locator('#camp-results')).toContainText('Campsite providers are busy');
  await expect(page.locator('#camp-search-links').getByRole('link',{name:'Google Maps'})).toBeVisible();
  await page.route('**/api/campsites/search?*',r => r.fulfill({json:{campsites:[],warnings:[]}}));
  await page.locator('#camp-form').getByRole('button',{name:'Find campsites'}).click();
  await expect(page.locator('#camp-results')).toContainText('No campsites found');
});

test('Park4Night sources, ratings, overnight filters and saved choice',async ({page}) => {
  await fixtures(page);await newTrip(page);
  await page.locator('#add-place').click();await choosePlace(page,'Nice');
  const camp = {id:'park4night/55',name:'Sunny Park4Night Camping',latitude:43.7,longitude:7.27,distanceKm:1,type:'camp_site',website:null,phone:null,facilities:['Showers'],source:'Park4Night',sourceUrl:'https://park4night.com/en/place/55',rating:4.5,reviewCount:12,otherSources:[{name:'OpenStreetMap',url:'https://www.openstreetmap.org/way/123'}]};
  await page.route('**/api/campsites/search?*',r => r.fulfill({json:{campsites:[camp,{...camp,id:'park4night/56',name:'Coast parking',type:'overnight_parking',category:'Parking day/night',sourceUrl:'https://park4night.com/en/place/56',otherSources:[]}],warnings:['One provider is busy; available results are shown.']}}));
  await page.locator('.camp-button').click();
  await expect(page.locator('.camp-card')).toHaveCount(1);
  await expect(page.locator('.camp-rating')).toHaveText('★ 4.5/5 · 12 reviews');
  await expect(page.locator('.provider-warning')).toContainText('One provider is busy');
  await expect(page.locator('.camp-card').getByRole('link',{name:'Park4Night'})).toHaveAttribute('href','https://park4night.com/en/place/55');
  await expect(page.locator('.camp-card').getByRole('link',{name:'OpenStreetMap'})).toHaveAttribute('href','https://www.openstreetmap.org/way/123');
  await page.locator('#camp-type').selectOption('other');await expect(page.locator('.camp-card h3')).toHaveText('Coast parking');
  await page.locator('#camp-type').selectOption('any');await expect(page.locator('.camp-card')).toHaveCount(2);
  await page.locator('#camp-source').selectOption('OpenStreetMap');await expect(page.locator('.camp-card')).toHaveCount(1);
  await page.locator('#camp-source').selectOption('Park4Night');await expect(page.locator('.camp-card')).toHaveCount(2);
  await page.locator('#camp-type').selectOption('camp_site');
  await page.screenshot({path:'../../TestResults/park4night-search.png',fullPage:true});
  await page.getByRole('button',{name:'Stay here',exact:true}).click();
  await page.locator('[data-close="camp-dialog"]').click();
  await page.locator('#save-trip').click();await expect(page.locator('#save-state')).toHaveText('All changes saved');
  await page.reload();await expect(page.locator('.selected-camp')).toContainText('Sunny Park4Night Camping');
});


test('trip dates can be created, edited, validated and cleared',async ({page}) => {
  await fixtures(page); await newTrip(page);
  await page.locator('#trip-settings').click();
  await expect(page.locator('#trip-date')).toHaveValue('2027-04-24');
  await page.locator('#trip-end-date').fill('2027-04-23');
  await page.locator('#trip-form').getByRole('button',{name:'Save trip',exact:true}).click();
  await expect(page.locator('#trip-dialog')).toBeVisible();
  expect(await page.locator('#trip-end-date').evaluate(el => el.validity.valid)).toBe(false);
  await page.locator('#trip-end-date').fill('2027-05-08');
  await page.locator('#trip-form').getByRole('button',{name:'Save trip',exact:true}).click();
  await expect(page.locator('#trip-dialog')).toBeHidden();
  await expect(page.locator('#summary-end')).toContainText('2027');
  await page.reload();
  await page.locator('#trip-settings').click();
  await expect(page.locator('#trip-date')).toHaveValue('2027-04-24');
  await expect(page.locator('#trip-end-date')).toHaveValue('2027-05-08');
  await page.locator('#trip-date').fill('2027-05-08');
  await page.locator('#trip-form').getByRole('button',{name:'Save trip',exact:true}).click();
  await expect(page.locator('#trip-dialog')).toBeHidden();
  await page.locator('#trip-settings').click();
  await page.locator('#trip-date').fill(''); await page.locator('#trip-end-date').fill('');
  await page.locator('#trip-form').getByRole('button',{name:'Save trip',exact:true}).click();
  await expect(page.locator('#summary-date')).toHaveText('Start date to be decided');
  await page.reload(); await page.locator('#trip-settings').click();
  await expect(page.locator('#trip-date')).toHaveValue('');
  await expect(page.locator('#trip-end-date')).toHaveValue('');
});


test('round trip draws and counts the return leg to the shared start and finish',async ({page}) => {
  await fixtures(page); await newTrip(page);
  const requests=[];
  await page.route('**/api/route',r => {
    const {points}=r.request().postDataJSON(); requests.push(points);
    const legs=points.slice(1).map((p,i) => ({distanceKm:i===points.length-2 ? 175 : 100,durationHours:2,coordinates:[[points[i].latitude,points[i].longitude],[p.latitude,p.longitude]],midpoint:p}));
    return r.fulfill({json:{distanceKm:legs.reduce((sum,l)=>sum+l.distanceKm,0),durationHours:2*legs.length,coordinates:points.map(p=>[p.latitude,p.longitude]),legs}});
  });
  await page.locator('#set-start').click(); await choosePlace(page,'Ermelo');
  await page.locator('#add-place').click(); await choosePlace(page,'Nice');
  await expect(page.locator('#summary-distance')).toHaveText('175 km');
  await page.locator('#return-home').click();
  await expect(page.locator('#summary-distance')).toHaveText('275 km');
  expect(requests.at(-1)).toEqual([places.Ermelo,places.Nice,places.Ermelo].map(({latitude,longitude})=>({latitude,longitude})));
  await expect(page.locator('#map .route-leg')).toHaveCount(2);
  await expect(page.locator('#map .route-final')).toHaveAttribute('stroke-dasharray','10,8');
  await expect(page.locator('#map .map-end')).toHaveText('S/F');
  await expect(page.locator('#map-finish-label')).toHaveText('Return to start');
  await expect(page.locator('.drive-leg').last()).toContainText('175 km');
  await expect(page.locator('.drive-leg').last()).toContainText('Nice → Ermelo');
  await page.locator('#save-trip').click(); await expect(page.locator('#save-state')).toHaveText('All changes saved');
  await page.reload();
  await expect(page.locator('#summary-distance')).toHaveText('275 km');
  await expect(page.locator('#map .route-final')).toHaveCount(1);
  await page.locator('#map .route-final').dispatchEvent('click');
  await expect(page.locator('#map-leg-title')).toHaveText('Nice → Ermelo');
  await expect(page.locator('#map-leg-distance')).toContainText('175 km');
  await page.locator('#map-leg-insert').click();
  await expect(page.locator('#insert-description')).toContainText('Nice → Ermelo · 175 km');
});


test('distance buttons select only their map leg, support keyboard and clear stale highlights',async ({page}) => {
  await fixtures(page); await newTrip(page);
  await page.locator('#set-start').click(); await choosePlace(page,'Ermelo');
  await page.locator('#add-place').click(); await choosePlace(page,'Milan');
  await page.locator('#add-place').click(); await choosePlace(page,'Nice');
  await page.locator('#return-home').click();
  await expect(page.locator('#summary-distance')).toHaveText('300 km');
  const distance = page.locator('[data-focus-leg="2"]');
  await distance.click();
  await expect(distance).toHaveAttribute('aria-pressed','true');
  await expect(page.locator('#map .route-selected')).toHaveCount(1);
  await expect(page.locator('#map .route-final')).toHaveAttribute('stroke','#7535bf');
  await expect(page.locator('#map-leg-title')).toHaveText('Nice → Ermelo');
  await expect(page.locator('#map-leg-distance')).toHaveText('100 km · 1h 30m');
  await expect(page.locator('#insert-dialog')).toBeHidden();
  await page.screenshot({path:'../../TestResults/selected-return-leg.png',fullPage:true});
  await page.locator('[data-focus-leg="0"]').focus(); await page.keyboard.press('Enter');
  await expect(page.locator('#map-leg-title')).toHaveText('Ermelo → Milan');
  await expect(distance).toHaveAttribute('aria-pressed','false');
  await expect(page.locator('#map .route-final')).toHaveAttribute('stroke-opacity','0.2');
  await expect(page.locator('#map .route-selected')).toHaveCount(1);
  await page.locator('#clear-leg-selection').click();
  await expect(page.locator('#map .route-selected')).toHaveCount(0);
  await expect(page.locator('#map-leg-selection')).toBeHidden();
  await distance.click();
  await page.locator('.stop-card').first().getByRole('button',{name:'Move Milan later'}).click();
  await expect(page.locator('#map-leg-selection')).toBeHidden();
  await expect(page.locator('#map .route-selected')).toHaveCount(0);
  await page.setViewportSize({width:390,height:844});
  await expect(page.locator('[data-focus-leg="2"]')).toBeEnabled();
  await page.locator('[data-focus-leg="2"]').click();
  await expect(page.locator('#map-leg-selection')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({path:'../../TestResults/selected-leg-mobile.png',fullPage:true});
});
