// Starts the whole stack hermetically: random ports everywhere, so it runs next
// to a live `dev.ps1` stack and in CI alike. Returns the teardown.
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { GenericContainer, Wait } from 'testcontainers'
import { setupJellyfin } from './jellyfin'
import { freePort, logs, root, start, waitHttp } from './stack'

// Image pins live in docker-compose.yml (one source of truth, never `latest`).
const compose = fs.readFileSync(path.join(root, 'docker-compose.yml'), 'utf8')
const pin = (name: string) =>
  compose.match(new RegExp(`image: (${name}:\\S+)`))![1]

export default async function globalSetup() {
  fs.rmSync(logs, { recursive: true, force: true })
  fs.mkdirSync(logs, { recursive: true })
  const build = (cmd: string, args: string[], cwd: string) =>
    execFileSync(cmd, args, { cwd, stdio: 'inherit' })

  // Firebase emulator image: same Dockerfile as dev + the backend test harness.
  const firebaseImage = await GenericContainer.fromDockerfile(
    path.join(root, 'dev', 'firebase'),
  ).build('r8er-firebase-emulator:e2e', { deleteOnExit: false })

  // TURN for the forced-relay step. Fixed host ports: coturn advertises
  // 127.0.0.1 (--external-ip), so host and container ports must match. Offset
  // from compose's coturn (3478, 49160+) so a dev relay stack can keep running.
  const turnPort = 3479
  const relayPorts = [49210, 49211, 49212, 49213, 49214]
  const fixed = (port: number, protocol: 'tcp' | 'udp') => ({
    container: port,
    host: port,
    protocol,
  })

  const [pg, firebase, jellyfin, coturn] = await Promise.all([
    new GenericContainer(pin('postgres'))
      .withEnvironment({
        POSTGRES_USER: 'r8er',
        POSTGRES_PASSWORD: 'dev',
        POSTGRES_DB: 'r8er',
      })
      .withExposedPorts(5432)
      // Twice: the init run restarts the server once before it's really ready.
      .withWaitStrategy(
        Wait.forLogMessage(/database system is ready to accept connections/, 2),
      )
      .start(),
    firebaseImage
      .withCopyFilesToContainer([
        {
          source: path.join(root, 'dev', 'firebase', 'firebase.json'),
          target: '/app/firebase.json',
        },
      ])
      .withWorkingDir('/app')
      .withCommand([
        'firebase',
        'emulators:start',
        '--only',
        'auth',
        '--project',
        'demo-r8er',
      ])
      .withExposedPorts(9099)
      .withWaitStrategy(Wait.forLogMessage('All emulators ready'))
      .start(),
    new GenericContainer(pin('jellyfin/jellyfin'))
      .withExposedPorts(8096)
      .withWaitStrategy(
        Wait.forHttp('/System/Info/Public', 8096).forStatusCode(200),
      )
      .withStartupTimeout(180_000)
      .start(),
    new GenericContainer(pin('coturn/coturn'))
      .withCommand([
        'turnserver',
        '-n',
        '--lt-cred-mech',
        '--user',
        'e2e:e2e',
        '--realm',
        'r8er.e2e',
        '--listening-port',
        String(turnPort),
        '--min-port',
        String(relayPorts[0]),
        '--max-port',
        String(relayPorts.at(-1)),
        '--external-ip=127.0.0.1',
        '--verbose',
      ])
      .withExposedPorts(
        fixed(turnPort, 'tcp'),
        fixed(turnPort, 'udp'),
        ...relayPorts.map((p) => fixed(p, 'udp')),
      )
      .withLogConsumer((stream) =>
        stream.pipe(fs.createWriteStream(path.join(logs, 'coturn.log'))),
      )
      // One line, printed once (listener lines are per thread and vary by host).
      .withWaitStrategy(Wait.forLogMessage(/Relay ports initialization done/))
      .start(),
    // Build while the containers boot.
    (async () => {
      build(
        'dotnet',
        ['build', 'src/R8er.Api', '--nologo', '-v', 'q'],
        path.join(root, 'backend'),
      )
      const bin = path.join(
        os.tmpdir(),
        `r8er-e2e-gateway${process.platform === 'win32' ? '.exe' : ''}`,
      )
      build(
        'go',
        ['build', '-o', bin, './cmd/gateway'],
        path.join(root, 'gateway'),
      )
      process.env.E2E_GATEWAY_BIN = bin
    })(),
  ])

  const emulator = `${firebase.getHost()}:${firebase.getMappedPort(9099)}`
  process.env.E2E_JELLYFIN_URL = `http://${jellyfin.getHost()}:${jellyfin.getMappedPort(8096)}`
  // Fixture media, made in the container by Jellyfin's own ffmpeg (no host
  // ffmpeg in CI): a 30 s H.264/AAC clip + sidecar English subtitle, in
  // Jellyfin's one-folder-per-movie layout.
  const sh = (cmd: string) =>
    jellyfin.exec(['sh', '-c', cmd]).then((r) => {
      if (r.exitCode !== 0) throw new Error(`jellyfin exec: ${r.output}`)
    })
  await sh(
    'D="/media/r8er e2e clip" && mkdir -p "$D" && /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -y ' +
      '-f lavfi -i testsrc=duration=30:size=640x360:rate=30 -f lavfi -i sine=frequency=440:duration=30 ' +
      '-c:v libx264 -pix_fmt yuv420p -c:a aac -shortest -metadata title="r8er e2e clip" "$D/r8er e2e clip.mp4" && ' +
      `printf '1\n00:00:00,000 --> 00:00:30,000\nr8er e2e subtitle\n' > "$D/r8er e2e clip.en.srt"`,
  )
  process.env.E2E_JELLYFIN_API_KEY = await setupJellyfin(
    process.env.E2E_JELLYFIN_URL,
  )

  const apiPort = await freePort()
  const api = `http://127.0.0.1:${apiPort}`
  const apiDir = path.join(root, 'backend', 'src', 'R8er.Api')
  // Picked before the backend starts: it's the backend's CORS allowlist entry.
  const appPort = await freePort()
  process.env.E2E_APP_URL = `http://127.0.0.1:${appPort}`
  const backend = start(
    'backend',
    'dotnet',
    ['bin/Debug/net10.0/R8er.Api.dll'],
    {
      cwd: apiDir,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: api,
        ConnectionStrings__Default: `Host=${pg.getHost()};Port=${pg.getMappedPort(5432)};Database=r8er;Username=r8er;Password=dev`,
        FIREBASE_AUTH_EMULATOR_HOST: emulator,
        FIREBASE_PROJECT_ID: 'demo-r8er',
        CORS_ORIGINS: process.env.E2E_APP_URL,
        // /turn hands these out (dev path; prod mints Cloudflare creds).
        R8ER_TURN_URLS: `turn:127.0.0.1:${turnPort}?transport=udp`,
        R8ER_TURN_USERNAME: 'e2e',
        R8ER_TURN_CREDENTIAL: 'e2e',
      },
    },
  )

  const vite = start(
    'vite',
    process.execPath,
    [
      'node_modules/vite/bin/vite.js',
      '--host',
      '127.0.0.1',
      '--port',
      String(appPort),
      '--strictPort',
    ],
    {
      cwd: path.join(root, 'frontend'),
      env: {
        VITE_API_URL: api,
        VITE_FIREBASE_AUTH_EMULATOR: `http://${emulator}`,
      },
    },
  )

  await Promise.all([
    waitHttp(`${api}/healthz`, backend),
    waitHttp(process.env.E2E_APP_URL, vite),
  ])

  return async () => {
    await Promise.all([backend.stop(), vite.stop()])
    // Ryuk reaps the containers even if this never runs (crash, Ctrl+C).
    await Promise.all([
      pg.stop(),
      firebase.stop(),
      jellyfin.stop(),
      coturn.stop(),
    ])
  }
}
