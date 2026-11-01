import { useEffect, useState } from 'react'
import {
  createEnrollmentToken,
  getDevices,
  type Device,
  type Install,
} from './api'
import { Pairing } from '../pairing/Pairing'

// Devices list + "Add agent" (onboarding flow 1). Polls so a freshly installed
// agent shows up, and online/offline stays current. ponytail: 5s poll, push
// updates over a socket if the list ever gets big.
export function Devices() {
  const [devices, setDevices] = useState<Device[]>([])
  const [install, setInstall] = useState<Install | null>(null)
  const [err, setErr] = useState<string | null>(null)

  useEffect(() => {
    const load = () =>
      getDevices()
        .then((d) => {
          setDevices(d)
          setErr(null)
        })
        .catch((e: Error) => setErr(e.message))
    load()
    const t = setInterval(load, 5000)
    return () => clearInterval(t)
  }, [])

  async function addAgent() {
    try {
      setInstall(await createEnrollmentToken())
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <section>
      <h2>Devices</h2>
      {devices.length === 0 ? (
        <p>No agents yet.</p>
      ) : (
        <ul style={{ listStyle: 'none', padding: 0 }}>
          {devices.map((d) => (
            <li key={d.id}>
              <span style={{ color: d.online ? 'green' : '#888' }}>●</span>{' '}
              {d.name ?? 'agent'}{' '}
              <code style={{ fontSize: '0.8em', color: '#888' }}>{d.id}</code> —{' '}
              {d.online ? 'online' : 'offline'}
              {d.online && d.lanUrl && (
                <>
                  {' · '}
                  <a href={d.lanUrl} target="_blank" rel="noreferrer">
                    Open agent page
                  </a>
                </>
              )}
              {d.online && <Pairing deviceId={d.id} lanUrl={d.lanUrl} />}
            </li>
          ))}
        </ul>
      )}
      <button type="button" onClick={addAgent}>
        Add agent
      </button>
      {install && (
        <div>
          <p>
            Run this on your NAS/PC (one-time token, expires{' '}
            {new Date(install.expiresAt).toLocaleTimeString()}):
          </p>
          {[
            ['bash', install.installCommand],
            ['PowerShell', install.installCommandPowershell],
          ].map(([label, cmd]) => (
            <div key={label}>
              <b>{label}</b> (from <code>agent/</code>)
              <pre style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                {cmd}
              </pre>
              <button
                type="button"
                onClick={() => navigator.clipboard.writeText(cmd)}
              >
                Copy
              </button>
            </div>
          ))}
        </div>
      )}
      {err && <p style={{ color: 'crimson' }}>{err}</p>}
    </section>
  )
}
