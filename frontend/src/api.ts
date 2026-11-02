import { auth } from './firebase'

export const API = import.meta.env.VITE_API_URL ?? 'http://localhost:5028'

export async function authed<T>(path: string, init?: RequestInit): Promise<T> {
  const token = await auth.currentUser?.getIdToken()
  const res = await fetch(`${API}${path}`, {
    ...init,
    headers: {
      ...(init?.body !== undefined && { 'Content-Type': 'application/json' }),
      ...(token && { Authorization: `Bearer ${token}` }),
    },
  })
  if (!res.ok) throw new Error(`${path} → ${res.status}`)
  return (res.status === 204 ? null : res.json()) as Promise<T>
}

export const getMe = () =>
  authed<{ userId: string; email: string; tenantId: string }>('/me')
