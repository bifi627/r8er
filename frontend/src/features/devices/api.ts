import { authed } from '../../api'

export type Device = {
  id: string
  name: string | null
  lastSeenAt: string | null
  lanUrl: string | null
  online: boolean
}

export type Install = {
  token: string
  expiresAt: string
  installCommand: string
  installCommandPowershell: string
}

export const getDevices = () => authed<Device[]>('/devices')

export const createEnrollmentToken = () =>
  authed<Install>('/agents/enrollment-tokens', { method: 'POST' })
