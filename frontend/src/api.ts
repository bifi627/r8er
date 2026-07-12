import { auth } from './firebase'

const API = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'

async function authed<T>(path: string): Promise<T> {
  const token = await auth.currentUser?.getIdToken()
  const res = await fetch(`${API}${path}`, {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  })
  if (!res.ok) throw new Error(`${path} → ${res.status}`)
  return res.json() as Promise<T>
}

export const getMe = () => authed<{ userId: string; email: string; tenantId: string }>('/me')
export const getDevices = () => authed<{ id: string; name: string | null; lastSeenAt: string | null }[]>('/devices')
