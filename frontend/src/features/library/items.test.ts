// Run: node --test src/features/library/items.test.ts (part of scripts/check.sh).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { clock, rangeHeader } from './items.ts'

test('hls.js exclusive range end → inclusive HTTP Range', () => {
  assert.equal(rangeHeader(), undefined)
  assert.equal(rangeHeader(0, 100), 'bytes=0-99')
  assert.equal(rangeHeader(500), 'bytes=500-')
  assert.equal(rangeHeader(undefined, 10), 'bytes=0-9')
})

test('clock formats seconds as m:ss', () => {
  assert.equal(clock(0), '0:00')
  assert.equal(clock(65.9), '1:05')
})
