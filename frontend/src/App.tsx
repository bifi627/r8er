import { useEffect, useState } from 'react'
import {
  onAuthStateChanged,
  signInWithEmailAndPassword,
  createUserWithEmailAndPassword,
  signOut,
  type User,
} from 'firebase/auth'
import { auth } from './firebase'
import { getMe, getDevices } from './api'

function SignIn() {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [err, setErr] = useState<string | null>(null)

  async function go(kind: 'in' | 'up') {
    setErr(null)
    try {
      const fn =
        kind === 'in'
          ? signInWithEmailAndPassword
          : createUserWithEmailAndPassword
      await fn(auth, email, password)
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <main
      style={{ maxWidth: 320, margin: '4rem auto', display: 'grid', gap: 8 }}
    >
      <h1>r8er</h1>
      <input
        placeholder="email"
        value={email}
        onChange={(e) => setEmail(e.target.value)}
      />
      <input
        placeholder="password"
        type="password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
      />
      <div style={{ display: 'flex', gap: 8 }}>
        <button type="button" onClick={() => go('in')}>
          Sign in
        </button>
        <button type="button" onClick={() => go('up')}>
          Create account
        </button>
      </div>
      {err && <p style={{ color: 'crimson' }}>{err}</p>}
    </main>
  )
}

function Shell({ user }: { user: User }) {
  const [tenantId, setTenantId] = useState('')
  const [devices, setDevices] = useState<{ id: string; name: string | null }[]>(
    [],
  )

  useEffect(() => {
    getMe()
      .then((m) => setTenantId(m.tenantId))
      .catch(() => {})
    getDevices()
      .then(setDevices)
      .catch(() => {})
  }, [])

  return (
    <main style={{ maxWidth: 480, margin: '4rem auto' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between' }}>
        <span>{user.email}</span>
        <button type="button" onClick={() => signOut(auth)}>
          Sign out
        </button>
      </header>
      <p style={{ color: '#888', fontSize: 12 }}>tenant {tenantId}</p>
      <h2>Devices</h2>
      {devices.length === 0 ? (
        <p>No devices yet — pairing comes next.</p>
      ) : (
        <ul>
          {devices.map((d) => (
            <li key={d.id}>{d.name ?? d.id}</li>
          ))}
        </ul>
      )}
    </main>
  )
}

export default function App() {
  const [user, setUser] = useState<User | null>(null)
  const [ready, setReady] = useState(false)

  useEffect(
    () =>
      onAuthStateChanged(auth, (u) => {
        setUser(u)
        setReady(true)
      }),
    [],
  )

  if (!ready) return null
  return user ? <Shell user={user} /> : <SignIn />
}
