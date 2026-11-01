// Process helpers shared by global-setup.ts and the specs. Every process logs to
// e2e/logs/<name>.log (uploaded by CI on failure) — the first place to look.
import { spawn, spawnSync, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'

export const root = path.resolve(import.meta.dirname, '..')
export const logs = path.join(import.meta.dirname, 'logs')

export type Proc = { proc: ChildProcess; stop: () => Promise<void> }

export function start(
  name: string,
  cmd: string,
  args: string[],
  opts: { cwd: string; env?: Record<string, string> },
): Proc {
  fs.mkdirSync(logs, { recursive: true })
  const log = fs.createWriteStream(path.join(logs, `${name}.log`), {
    flags: 'a',
  })
  const win = process.platform === 'win32'
  // detached on POSIX = own process group, so stop() takes children with it.
  const proc = spawn(cmd, args, {
    cwd: opts.cwd,
    env: { ...process.env, ...opts.env },
    detached: !win,
  })
  proc.stdout!.pipe(log)
  proc.stderr!.pipe(log)
  const stop = async () => {
    if (proc.exitCode !== null || proc.signalCode !== null) return
    const exited = new Promise((r) => proc.once('exit', r))
    if (win) spawnSync('taskkill', ['/T', '/F', '/PID', String(proc.pid)])
    else process.kill(-proc.pid!, 'SIGTERM')
    await exited
  }
  return { proc, stop }
}

/** Polls url until it answers 2xx; fails early if the process died. */
export async function waitHttp(url: string, p: Proc, timeoutMs = 120_000) {
  const end = Date.now() + timeoutMs
  while (Date.now() < end) {
    if (p.proc.exitCode !== null)
      throw new Error(
        `${url}: process exited ${p.proc.exitCode} (see e2e/logs)`,
      )
    try {
      if ((await fetch(url)).ok) return
    } catch {
      // not listening yet
    }
    await new Promise((r) => setTimeout(r, 500))
  }
  throw new Error(`${url}: not up after ${timeoutMs}ms (see e2e/logs)`)
}

export function freePort() {
  return new Promise<number>((resolve, reject) => {
    const s = net.createServer().listen(0, '127.0.0.1', () => {
      const { port } = s.address() as net.AddressInfo
      s.close(() => resolve(port))
    })
    s.on('error', reject)
  })
}

/**
 * Runs the agent binary built in global setup. stateDir survives stop/start
 * (that's the cert-swap step); the token is only needed for the first run.
 */
export async function startAgent(
  stateDir: string,
  env: Record<string, string>,
) {
  const port = await freePort()
  const pageUrl = `http://127.0.0.1:${port}`
  const p = start('agent', process.env.E2E_AGENT_BIN!, [], {
    cwd: root,
    env: {
      R8ER_STATE_DIR: stateDir,
      R8ER_PAGE_PORT: String(port),
      R8ER_LAN_URL: pageUrl,
      R8ER_ORIGIN: process.env.E2E_JELLYFIN_URL!,
      ...env,
    },
  })
  await waitHttp(pageUrl, p)
  return { ...p, pageUrl }
}
