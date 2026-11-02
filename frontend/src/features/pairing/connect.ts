// Connect to a gateway through the tenant broker, enforce the pin, prove this
// browser's client key, and (when pairing) run commit/reveal for the
// comparison code. docs/protocol.md, "Broker" + "Pairing".
import { auth } from '../../firebase'
import { API } from '../../api'
import {
  authMessage,
  b64,
  comparisonCode,
  hex,
  nonce,
  pinCheck,
  sdpFingerprint,
  sha256,
} from './crypto'
import { clientKey, getPin, setPin } from './store'

export class PinMismatch extends Error {}

export type Session = { pc: RTCPeerConnection; close: () => void }

type Msg = Record<string, string | undefined> & { type: string }

/**
 * Resolves once the gateway lets this browser use the tunnel. With onCode set, an
 * unpaired browser starts pairing and onCode receives the code to compare.
 */
export async function connect(
  gatewayId: string,
  onCode?: (code: string) => void,
): Promise<Session> {
  const [key, pin, token] = await Promise.all([
    clientKey(),
    getPin(gatewayId),
    auth.currentUser?.getIdToken(),
  ])
  const ws = new WebSocket(API.replace(/^http/, 'ws') + '/broker/ws')
  // ponytail: STUN only; TURN credentials for the MVP client arrive with item 6.
  const pc = new RTCPeerConnection({
    iceServers: [{ urls: 'stun:stun.l.google.com:19302' }],
  })
  const close = () => {
    pc.close()
    ws.close() // the broker tells the gateway `bye`
  }
  const send = (m: object) => ws.send(JSON.stringify(m))

  return new Promise<Session>((resolve, reject) => {
    const fail = (e: Error) => {
      close()
      reject(e)
    }
    let gatewayFp = ''
    let verdict: ReturnType<typeof pinCheck> = 'refuse'
    let nB = ''

    ws.onopen = () => send({ type: 'auth', token })
    ws.onclose = () => fail(new Error('broker connection closed'))
    // Handle broker messages strictly in order. The gateway can still trickle a
    // candidate before its answer (Pion gathers during SetLocalDescription), so
    // candidates that beat the answer wait in `early`.
    let queue = Promise.resolve()
    const early: RTCIceCandidateInit[] = []
    ws.onmessage = (e) => {
      queue = queue.then(() => onBroker(JSON.parse(e.data))).catch(fail)
    }
    const onBroker = async (m: { type: string; [k: string]: any }) => {
      if (m.type === 'ready') {
        await pc.setLocalDescription(await pc.createOffer())
        send({ type: 'offer', gatewayId, sdp: pc.localDescription })
      } else if (m.type === 'answer') {
        gatewayFp = sdpFingerprint(m.sdp.sdp)
        // Before a single byte goes to the gateway: an unexpected certificate means
        // someone (possibly r8er's own server) is in the middle. Pairing again is
        // allowed: the comparison code authenticates the new certificate.
        verdict = pinCheck(pin, gatewayFp, !!onCode)
        if (verdict === 'refuse')
          return fail(
            new PinMismatch(
              `gateway certificate changed: pinned ${pin}, got ${gatewayFp}`,
            ),
          )
        await pc.setRemoteDescription(m.sdp)
        for (const c of early.splice(0)) await pc.addIceCandidate(c)
      } else if (m.type === 'ice') {
        if (pc.remoteDescription) await pc.addIceCandidate(m.candidate)
        else early.push(m.candidate)
      } else if (m.type === 'error') {
        fail(new Error(m.error))
      }
    }
    pc.onicecandidate = (e) =>
      e.candidate && send({ type: 'ice', candidate: e.candidate.toJSON() })

    const ch = pc.createDataChannel('auth')
    const say = (m: object) => ch.send(JSON.stringify(m))
    ch.onclose = () => fail(new Error('gateway closed the connection'))
    ch.onmessage = (e) => onGateway(JSON.parse(e.data)).catch(fail)
    const onGateway = async (m: Msg) => {
      switch (m.type) {
        case 'challenge': {
          const browserFp = sdpFingerprint(pc.localDescription!.sdp)
          const sig = await crypto.subtle.sign(
            { name: 'ECDSA', hash: 'SHA-256' },
            key.privateKey,
            authMessage(gatewayFp, browserFp, m.n!),
          )
          // No matching pin (new browser, or the gateway's cert changed): ask for
          // the code comparison even if the gateway knows our key — "ok" alone
          // can't vouch for a certificate we haven't verified.
          return say({
            type: 'auth',
            spki: b64(key.spki),
            sig: b64(sig),
            pair: verdict !== 'pinned',
          })
        }
        case 'ok':
          if (verdict !== 'pinned')
            return fail(
              new PinMismatch(
                'gateway skipped pairing for an unpinned certificate',
              ),
            )
          ch.onclose = null
          return resolve({ pc, close })
        case 'unpaired':
          if (!onCode)
            return fail(
              new Error('this browser is not paired with the gateway'),
            )
          nB = nonce()
          return say({
            type: 'commit',
            h: hex(await sha256(nB)),
            name: browserName(),
          })
        case 'nonce':
          say({ type: 'reveal', n: nB })
          return onCode?.(await comparisonCode(gatewayFp, key.fp, m.n!, nB))
        case 'approved':
          await setPin(gatewayId, gatewayFp)
          ch.onclose = null
          return resolve({ pc, close })
        case 'rejected':
          return fail(new Error(`gateway refused: ${m.reason ?? 'rejected'}`))
      }
    }
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
