// IndexedDB: this browser's device key (non-extractable CryptoKey — script on
// the page can use it but never read it out) and the agent pins, per device id.
import { fingerprint } from './crypto'

const db = new Promise<IDBDatabase>((resolve, reject) => {
  const r = indexedDB.open('r8er', 1)
  r.onupgradeneeded = () => r.result.createObjectStore('kv')
  r.onsuccess = () => resolve(r.result)
  r.onerror = () => reject(r.error)
})

async function kv<T>(
  mode: IDBTransactionMode,
  f: (s: IDBObjectStore) => IDBRequest,
) {
  const s = (await db).transaction('kv', mode).objectStore('kv')
  return new Promise<T>((resolve, reject) => {
    const r = f(s)
    r.onsuccess = () => resolve(r.result as T)
    r.onerror = () => reject(r.error)
  })
}

export type DeviceKey = { privateKey: CryptoKey; spki: ArrayBuffer; fp: string }

/** Created once per browser profile. Lost (site data cleared) = re-pair. */
export async function deviceKey(): Promise<DeviceKey> {
  const have = await kv<DeviceKey | undefined>('readonly', (s) =>
    s.get('deviceKey'),
  )
  if (have) return have
  const k = await crypto.subtle.generateKey(
    { name: 'ECDSA', namedCurve: 'P-256' },
    false,
    ['sign'],
  )
  const spki = await crypto.subtle.exportKey('spki', k.publicKey)
  const key = { privateKey: k.privateKey, spki, fp: await fingerprint(spki) }
  await kv('readwrite', (s) => s.put(key, 'deviceKey'))
  return key
}

export const getPin = (deviceId: string) =>
  kv<string | undefined>('readonly', (s) => s.get(`pin:${deviceId}`))

export const setPin = (deviceId: string, agentFp: string) =>
  kv('readwrite', (s) => s.put(agentFp, `pin:${deviceId}`))
