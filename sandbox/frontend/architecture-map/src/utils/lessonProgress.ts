// Lesson Mode's own progress tracking - which lessons have a completed run 2. Deliberately
// client-side only (localStorage), no backend persistence: it's per-browser learning progress,
// not stand state, and never needs to survive a different machine or a `stop-stack -Wipe`.
const STORAGE_KEY = 'lesson-mode-completed'

function readCompletedIds(): string[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    return raw ? (JSON.parse(raw) as string[]) : []
  } catch {
    return []
  }
}

export function loadLessonProgress(): string[] {
  return readCompletedIds()
}

export function markLessonCompleted(lessonId: string): void {
  try {
    const ids = readCompletedIds()
    if (!ids.includes(lessonId)) {
      localStorage.setItem(STORAGE_KEY, JSON.stringify([...ids, lessonId]))
    }
  } catch {
    // Best-effort - a blocked/full localStorage just means progress isn't remembered, never a
    // reason to break the lesson itself.
  }
}
