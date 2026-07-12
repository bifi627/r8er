import { initializeApp } from 'firebase/app'
import { getAuth, connectAuthEmulator } from 'firebase/auth'

// Dev uses the emulator (no real Firebase project needed). projectId is all the
// emulator requires; prod supplies the real apiKey/authDomain via VITE_ vars.
const app = initializeApp({
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY ?? 'demo-key',
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID ?? 'demo-r8er',
  authDomain:
    import.meta.env.VITE_FIREBASE_AUTH_DOMAIN ?? 'demo-r8er.firebaseapp.com',
})

export const auth = getAuth(app)

const emulator = import.meta.env.VITE_FIREBASE_AUTH_EMULATOR
if (emulator) connectAuthEmulator(auth, emulator, { disableWarnings: true })
