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
  await page.route('**/api/campsites?*',r => {
    const q=new URL(r.request().url()).searchParams;
    return r.fulfill({json:[{id:'way/123',name:'Sunny Coast Camping',latitude:Number(q.get('latitude'))+.01,longitude:Number(q.get('longitude'))+.01,distanceKm:1.4,type:'camp_site',website:'https://example.com',phone:null,facilities:['Showers','Water'],access:'customers'}]});
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
  await page.route('**/api/campsites?*',r => r.fulfill({status:503,json:{detail:'Campsite providers are busy. Try again.'}}));
  await page.locator('.camp-button').click();
  await expect(page.locator('#camp-results')).toContainText('Campsite providers are busy');
  await expect(page.locator('#camp-search-links').getByRole('link',{name:'Google Maps'})).toBeVisible();
  await page.route('**/api/campsites?*',r => r.fulfill({json:[]}));
  await page.locator('#camp-form').getByRole('button',{name:'Find campsites'}).click();
  await expect(page.locator('#camp-results')).toContainText('No campsites found');
});
