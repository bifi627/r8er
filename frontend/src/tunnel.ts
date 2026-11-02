// Tunnel framing (docs/protocol.md): one data channel per request. The request
// is one text frame, the response head one text frame, the body binary frames;
// the gateway closing the channel is EOF.

export type TunnelResponse = {
  status: number
  headers: Record<string, string>
  body: Uint8Array<ArrayBuffer>
}

export function tunnelFetch(
  pc: RTCPeerConnection,
  url: string,
  opts: {
    method?: string
    headers?: Record<string, string>
    body?: string
  } = {},
) {
  return new Promise<TunnelResponse>((resolve, reject) => {
    const dc = pc.createDataChannel(`req:${url.slice(0, 200)}`)
    dc.binaryType = 'arraybuffer'
    let head: { status: number; headers?: Record<string, string> } | null = null
    const chunks: Uint8Array[] = []
    let size = 0
    dc.onopen = () =>
      dc.send(
        JSON.stringify({
          method: opts.method ?? 'GET',
          url,
          headers: opts.headers ?? {},
          ...(opts.body !== undefined && { body: opts.body }),
        }),
      )
    dc.onmessage = (e) => {
      if (typeof e.data === 'string') head = JSON.parse(e.data)
      else {
        chunks.push(new Uint8Array(e.data))
        size += e.data.byteLength
      }
    }
    dc.onclose = () => {
      if (!head) return reject(new Error(`tunnel: no response for ${url}`))
      const body = new Uint8Array(size)
      let off = 0
      for (const c of chunks) {
        body.set(c, off)
        off += c.length
      }
      resolve({ status: head.status, headers: head.headers ?? {}, body })
    }
  })
}

/** JSON over the tunnel: GET, or POST when body is given. Throws on non-2xx. */
export async function tunnelJson<T>(
  pc: RTCPeerConnection,
  url: string,
  body?: unknown,
): Promise<T> {
  const r = await tunnelFetch(
    pc,
    url,
    body === undefined ? {} : { method: 'POST', body: JSON.stringify(body) },
  )
  if (r.status < 200 || r.status > 299)
    throw new Error(`${url.split('?')[0]} → HTTP ${r.status}`)
  const text = new TextDecoder().decode(r.body)
  return (text ? JSON.parse(text) : null) as T
}
