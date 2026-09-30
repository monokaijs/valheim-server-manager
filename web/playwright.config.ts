import { defineConfig } from '@playwright/test'
export default defineConfig({
  testDir: './tests', timeout: 30000, workers: 1,
  use: { baseURL: 'http://127.0.0.1:5187', headless: true, launchOptions: process.env.VSM_TEST_BROWSER ? { executablePath: process.env.VSM_TEST_BROWSER } : {}, viewport: { width: 1440, height: 1050 } },
  webServer: { command: 'npm run dev -- --host 127.0.0.1 --port 5187', url: 'http://127.0.0.1:5187', reuseExistingServer: false },
})
