// THE golden path (dev-env spec: exactly one E2E; edge cases belong in the
// integration layer). Real browser → broker → agent → Jellyfin. Item 5 extends
// this same story with media → browse → play → seek.
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { expect, test, type Browser, type Page } from '@playwright/test'
import { startAgent } from './stack'

const email = 'e2e@r8er.test'
const password = 'e2e-password'

async function signIn(browser: Browser, create = false) {
  const page = await (await browser.newContext()).newPage() // own IndexedDB = own device key
  await page.goto(process.env.E2E_APP_URL!)
  await page.getByPlaceholder('email').fill(email)
  await page.getByPlaceholder('password').fill(password)
  await page
    .getByRole('button', { name: create ? 'Create account' : 'Sign in' })
    .click()
  await expect(page.getByRole('heading', { name: 'Devices' })).toBeVisible()
  return page
}

const tunnelOk = (p: Page) =>
  expect(p.getByText('Tunnel OK · HTTP 200')).toBeVisible()

/** Pair from the web app; approve on the agent page only if the codes match. */
async function pair(app: Page, agentPage: Page, agentUrl: string) {
  await app.getByRole('button', { name: 'Pair this device' }).click()
  const code = (await app
    .locator('p', { hasText: /^Code/ })
    .locator('b')
    .textContent())!
  await agentPage.goto(agentUrl)
  const request = agentPage.locator('form[action="/decide"]')
  await expect(request).toContainText(code) // page self-refreshes every 3s
  await request.getByRole('button', { name: 'Approve' }).click()
  await tunnelOk(app)
}

test('onboard agent, pair, pin, re-pair after cert swap, remove', async ({
  browser,
}) => {
  const stateDir = fs.mkdtempSync(path.join(os.tmpdir(), 'r8er-e2e-agent-'))
  const a = await signIn(browser, true)

  // Onboarding: Add agent → run the shown install command.
  await a.getByRole('button', { name: 'Add agent' }).click()
  const cmd = (await a.locator('pre').first().textContent())!
  const env = Object.fromEntries(
    [...cmd.matchAll(/(R8ER_\w+)=(\S+)/g)].map((m) => [m[1], m[2]]),
  )
  // Without a server URL the agent falls back to POC mode and dials prod Railway.
  expect(env.R8ER_SERVER_URL).toMatch(/^http:\/\/127\.0\.0\.1:/)
  let agent = await startAgent(stateDir, env)
  try {
    await expect(a.getByRole('listitem')).toContainText('online')
    const agentPage = await (await browser.newContext()).newPage()

    await test.step('browser A pairs; codes match on both sides', () =>
      pair(a, agentPage, agent.pageUrl))

    await test.step('pin + key survive a reload (IndexedDB)', async () => {
      await a.reload()
      await a.getByRole('button', { name: 'Test connection' }).click()
      await tunnelOk(a)
    })

    await test.step('browser B (same account, never paired) is refused', async () => {
      const b = await signIn(browser)
      await b.getByRole('button', { name: 'Test connection' }).click()
      await expect(
        b.getByText('this browser is not paired with the agent'),
      ).toBeVisible()
    })

    await test.step('new agent certificate → red warning → re-pair', async () => {
      await agent.stop()
      fs.rmSync(path.join(stateDir, 'cert.pem'))
      // Same env; the spent token is ignored — agent.json already holds the credential.
      agent = await startAgent(stateDir, env)
      await a.getByRole('button', { name: 'Test connection' }).click()
      await expect(
        a.getByText('Refused: this is not the agent you paired with.'),
      ).toBeVisible()
      await pair(a, agentPage, agent.pageUrl)
    })

    await test.step('Remove on the agent page → A is refused', async () => {
      await agentPage.goto(agent.pageUrl)
      const paired = agentPage.locator('form[action="/remove"]')
      await expect(paired).toHaveCount(1)
      await paired.getByRole('button', { name: 'Remove' }).click()
      await expect(paired).toHaveCount(0)
      await a.getByRole('button', { name: 'Test connection' }).click()
      await expect(
        a.getByText('this browser is not paired with the agent'),
      ).toBeVisible()
    })
  } finally {
    await agent.stop()
  }
})
