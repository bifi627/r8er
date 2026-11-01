// Connect to an agent through the tenant broker, enforce the pin, prove this
// browser's device key, and (when pairing) run commit/reveal for the
// comparison code. docs/protocol.md, "Broker" + "Pairing".
import { auth } from '../../firebase'
import { API } from '../../api'
import {
  authMessage,
  b64,
  comparisonCode,
  hex,
  nonce,
  sdpFingerprint,
  sha256,
} from './crypto'
import { deviceKey, getPin, setPin } from './store'

export class PinMismatch extends Error {}

export type Session = { pc: RTCPeerConnection; close: () => void }

type Msg = Record<string, string | undefined> & { type: string }

/**
 * Resolves once the agent lets this browser use the tunnel. With onCode set, an
 * unpaired browser starts pairing and onCode receives the code to compare.
 */
export async function connect(
  deviceId: string,
  onCode?: (code: string) => void,
): Promise<Session> {
  const [key, pin, token] = await Promise.all([
    deviceKey(),
    getPin(deviceId),
    auth.currentUser?.getIdToken(),
  ])
  const ws = new WebSocket(API.replace(/^http/, 'ws') + '/broker/ws')
  // ponytail: STUN only; TURN credentials for the MVP client arrive with item 6.
  const pc = new RTCPeerConnection({
    iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
  })
  const close = () => {
    pc.close()
    ws.close() // the broker tells the agent `bye`
  }
  const send = (m: object) => ws.send(JSON.stringify(m))

  return new Promise<Session>((resolve, reject) => {
    const fail = (e: Error) => {
      close()
      reject(e)
    }
    let agentFp = ''
    let nB = ''

    ws.onopen = () => send({ type: 'auth', token })
    ws.onclose = () => fail(new Error('broker connection closed'))
    // Handle broker messages strictly in order: an ICE candidate must not race
    // ahead of the answer's setRemoteDescription.
    let queue = Promise.resolve()
    ws.onmessage = (e) => {
      queue = queue.then(() => onBroker(JSON.parse(e.data))).catch(fail)
    }
    const onBroker = async (m: { type: string; [k: string]: any }) => {
      if (m.type === 'ready') {
        await pc.setLocalDescription(await pc.createOffer())
        send({ type: 'offer', deviceId, sdp: pc.localDescription })
      } else if (m.type === 'answer') {
        agentFp = sdpFingerprint(m.sdp.sdp)
        // Before a single byte goes to the agent: an unexpected certificate means
        // someone (possibly r8er's own server) is in the middle. Pairing again is
        // allowed: the comparison code authenticates the new certificate.
        if (pin && pin !== agentFp && !onCode)
          return fail(
            new PinMismatch(
              `agent certificate changed: pinned ${pin}, got ${agentFp}`,
            ),
          )
        await pc.setRemoteDescription(m.sdp)
      } else if (m.type === 'ice') {
        await pc.addIceCandidate(m.candidate)
      } else if (m.type === 'error') {
        fail(new Error(m.error))
      }
    }
    pc.onicecandidate = (e) =>
      e.candidate && send({ type: 'ice', candidate: e.candidate.toJSON() })

    const ch = pc.createDataChannel('auth')
    const say = (m: object) => ch.send(JSON.stringify(m))
    ch.onclose = () => fail(new Error('agent closed the connection'))
    ch.onmessage = (e) => onAgent(JSON.parse(e.data)).catch(fail)
    const onAgent = async (m: Msg) => {
      switch (m.type) {
        case 'challenge': {
          const browserFp = sdpFingerprint(pc.localDescription!.sdp)
          const sig = await crypto.subtle.sign(
            { name: 'ECDSA', hash: 'SHA-256' },
            key.privateKey,
            authMessage(agentFp, browserFp, m.n!),
          )
          return say({ type: 'auth', spki: b64(key.spki), sig: b64(sig) })
        }
        case 'ok':
          if (!pin)
            return fail(
              new Error(
                'the agent knows this browser but it has no pin: remove it on the agent page and pair again',
              ),
            )
          ch.onclose = null
          return resolve({ pc, close })
        case 'unpaired':
          if (!onCode)
            return fail(new Error('this browser is not paired with the agent'))
          nB = nonce()
          return say({
            type: 'commit',
            h: hex(await sha256(nB)),
            name: browserName(),
          })
        case 'nonce':
          say({ type: 'reveal', n: nB })
          return onCode?.(await comparisonCode(agentFp, key.fp, m.n!, nB))
        case 'approved':
          await setPin(deviceId, agentFp)
          ch.onclose = null
          return resolve({ pc, close })
        case 'rejected':
          return fail(new Error(`agent refused: ${m.reason ?? 'rejected'}`))
      }
    }
  })
}

/** Tunnel framing (docs/protocol.md): one data channel per request. */
export function tunnelFetch(pc: RTCPeerConnection, url: string) {
  return new Promise<{ status: number; body: Blob }>((resolve, reject) => {
    const dc = pc.createDataChannel(`req:${url}`)
    dc.binaryType = 'arraybuffer'
    let status = 0
    const chunks: ArrayBuffer[] = []
    dc.onopen = () =>
      dc.send(JSON.stringify({ method: 'GET', url, headers: {} }))
    dc.onmessage = (e) => {
      if (typeof e.data === 'string') status = JSON.parse(e.data).status
      else chunks.push(e.data)
    }
    dc.onclose = () =>
      status
        ? resolve({ status, body: new Blob(chunks) })
        : reject(new Error('tunnel refused'))
  })
}

function browserName() {
  const ua = navigator.userAgent
  const b = /Edg\//.test(ua)
    ? 'Edge'
    : /Chrome\//.test(ua)
      ? 'Chrome'
      : /Firefox\//.test(ua)
        ? 'Firefox'
        : /Safari\//.test(ua)
          ? 'Safari'
          : 'Browser'
  const os = /iPhone|iPad/.test(ua)
    ? 'iOS'
    : /Android/.test(ua)
      ? 'Android'
      : /Windows/.test(ua)
        ? 'Windows'
        : /Mac/.test(ua)
          ? 'macOS'
          : /Linux/.test(ua)
            ? 'Linux'
            : ''
  return os ? `${b} on ${os}` : b
}
