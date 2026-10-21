const {defineConfig} = require('playwright/test');
module.exports = defineConfig({
  testDir:'.', timeout:60000, workers:1, reporter:'list',
  use:{baseURL:process.env.ROADTRIP_TEST_URL || 'http://127.0.0.1:5187',viewport:{width:1440,height:1000},
    launchOptions:process.env.ROADTRIP_BROWSER_EXECUTABLE ? {executablePath:process.env.ROADTRIP_BROWSER_EXECUTABLE,args:['--no-sandbox','--disable-dev-shm-usage','--disable-gpu','--no-zygote','--use-gl=angle','--use-angle=swiftshader']} : {}},
  webServer:process.env.ROADTRIP_TEST_URL ? undefined : {
    command:'dotnet run --project ../../src/RoadtripPlanner --no-build -c Release -- --urls http://127.0.0.1:5187 --Data:Directory=../../TestResults/browser-data',
    url:'http://127.0.0.1:5187/health',reuseExistingServer:false,timeout:30000
  }
});
