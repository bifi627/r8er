import { useState } from 'react'
import { connect, PinMismatch, tunnelFetch } from './connect'

// Pair this browser with an online gateway (flow 2), or open a pinned connection
// and tunnel one request through it — the item-3 done check. The real player
// replaces "Test connection" in item 5.
export function Pairing({
  gatewayId,
  lanUrl,
}: {
  gatewayId: string
  lanUrl: string | null
}) {
  const [code, setCode] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [alarm, setAlarm] = useState<string | null>(null)

  async function run(pair: boolean) {
    setCode(null)
    setAlarm(null)
    setStatus(pair ? 'Connecting…' : 'Connecting (pinned)…')
    try {
      const s = await connect(gatewayId, pair ? setCode : undefined)
      setCode(null)
      setStatus('Connected · tunneling GET /System/Info/Public…')
      const r = await tunnelFetch(s.pc, '/System/Info/Public')
      setStatus(
        `Tunnel OK · HTTP ${r.status} · ${(await r.body.text()).slice(0, 120)}`,
      )
      s.close()
    } catch (e) {
      setCode(null)
      if (e instanceof PinMismatch) {
        setStatus(null)
        setAlarm(e.message)
      } else setStatus(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <div style={{ marginLeft: '1.5rem' }}>
      <button type="button" onClick={() => run(true)}>
        Pair this browser
      </button>{' '}
      <button type="button" onClick={() => run(false)}>
        Test connection
      </button>
      {code && (
        <p>
          Code{' '}
          <b style={{ fontSize: '1.6em', letterSpacing: '0.1em' }}>{code}</b> —
          open the{' '}
          {lanUrl ? (
            <a href={lanUrl} target="_blank" rel="noreferrer">
              gateway page
            </a>
          ) : (
            'gateway page'
          )}{' '}
          on your home network and approve if it shows the same code.
        </p>
      )}
      {status && <p style={{ color: '#666' }}>{status}</p>}
      {alarm && (
        <p style={{ color: 'white', background: 'crimson', padding: '0.5rem' }}>
          <b>Refused: this is not the gateway you paired with.</b> {alarm}.
          Don't continue unless you re-installed the gateway yourself; then pair
          again.
        </p>
      )}
    </div>
  )
}
