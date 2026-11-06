import { useEffect, useState } from 'react'
import lessonsData from '../data/lessons.json'
import { applyConfigPatch, captureLessonLiveConfig, matchesPatch, type LessonLiveConfig } from '../utils/infraConfig'
import { loadLessonProgress } from '../utils/lessonProgress'
import { warmUpIfTracedRequest } from './LessonPanel'
import type { LessonsFile } from '../types/lessons'

const data = lessonsData as unknown as LessonsFile

interface Props {
  onClose: () => void
  onOpenLesson: (lessonId: string) => void
  // Same reasoning as PresetsModal's own onInfraChanged - "Set config" touches nginx/pgcat/cache/
  // messaging/etc, so Diagram's filter needs to refetch or it'd keep showing the pre-apply topology.
  onInfraChanged: () => void
}

// Global (not node-scoped) modal, same pattern as PresetsModal - opened from a header button in
// App.tsx. "Open" transitions into LessonPanel (mounted in app-sidebar-left), not another modal.
export function LessonsModal({ onClose, onOpenLesson, onInfraChanged }: Props) {
  const [live, setLive] = useState<LessonLiveConfig | null>(null)
  const [completedIds, setCompletedIds] = useState<string[]>([])
  const [settingConfigId, setSettingConfigId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    captureLessonLiveConfig().then(setLive).catch(() => {})
    setCompletedIds(loadLessonProgress())
  }, [])

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [onClose])

  async function setConfig(lessonId: string) {
    const lesson = data.lessons.find((l) => l.id === lessonId)
    if (!lesson) return
    setSettingConfigId(lessonId)
    setError(null)
    try {
      // Only ever the lesson's STARTING config, never its target/fix - jumping straight to "after"
      // would skip the reason the lesson exists (see .notes/PLAN.md's Lesson Mode entry).
      await applyConfigPatch(lesson.config.start)
      onInfraChanged()
      await warmUpIfTracedRequest(lesson)
      setLive(await captureLessonLiveConfig())
    } catch {
      setError('Failed to set this config - see the console for details.')
    } finally {
      setSettingConfigId(null)
    }
  }

  return (
    <div className="presets-modal-overlay" onClick={onClose}>
      <div className="presets-modal lessons-modal" onClick={(e) => e.stopPropagation()}>
        <div className="presets-modal-header">
          <h3>Lessons</h3>
          <button onClick={onClose} aria-label="Close">
            &times;
          </button>
        </div>

        <p className="presets-modal-note">Click &quot;Open&quot; once the live config matches, or jump straight there with &quot;Set config&quot;.</p>
        {error && <p className="service-card-error">{error}</p>}

        <div className="lessons-list-scroll">
          {data.chapters.map((chapter) => (
            <div key={chapter.id}>
              <div className="lessons-chapter-label">{chapter.title.toUpperCase()}</div>
              {data.lessons
                .filter((l) => l.chapterId === chapter.id)
                .map((lesson) => {
                  const done = completedIds.includes(lesson.id)
                  // Only the lesson's declared start-relevant keys are compared (see matchesPatch)
                  // - several lessons can show "current" at once, which is fine, not ambiguous: it
                  // just means more than one is reachable from here right now.
                  const isCurrent = live ? matchesPatch(live, lesson.config.start) : false
                  return (
                    <div key={lesson.id} className={isCurrent ? 'lessons-row lessons-row-current' : 'lessons-row'}>
                      <div className="lessons-row-main">
                        {done && <span className="lessons-row-status lessons-row-status-done">✓</span>}
                        {!done && !isCurrent && <span className="lessons-row-status" />}
                        <span className="lessons-row-title">
                          {isCurrent && !done && <span className="lessons-row-current-label">CURRENT CONFIG · </span>}
                          {lesson.id} {lesson.title}
                        </span>
                      </div>
                      <div className="lessons-row-actions">
                        <button onClick={() => setConfig(lesson.id)} disabled={settingConfigId === lesson.id}>
                          {settingConfigId === lesson.id ? 'Setting...' : 'Set config'}
                        </button>
                        <button className="lessons-row-open" disabled={!isCurrent} onClick={() => onOpenLesson(lesson.id)}>
                          Open →
                        </button>
                      </div>
                    </div>
                  )
                })}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
