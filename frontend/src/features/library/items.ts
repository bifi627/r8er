// Jellyfin item shape (the fields the UI reads) + pure helpers (no DOM, so
// node --test can import this file).
export type Item = {
  Id: string
  Name: string
  Type: string
  IsFolder?: boolean
  SeriesId?: string
  RunTimeTicks?: number
  ProductionYear?: number
  IndexNumber?: number
  ImageTags?: { Primary?: string }
  UserData?: { PlaybackPositionTicks?: number; Played?: boolean }
}

export const clock = (s: number) =>
  `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`

/** hls.js ranges have an exclusive end; HTTP Range's end is inclusive. */
export function rangeHeader(start?: number, end?: number) {
  if (start == null && end == null) return undefined
  return `bytes=${start ?? 0}-${end ? end - 1 : ''}`
}

/** Jellyfin time unit: 100 ns ticks. */
export const TICKS_PER_SECOND = 10_000_000
