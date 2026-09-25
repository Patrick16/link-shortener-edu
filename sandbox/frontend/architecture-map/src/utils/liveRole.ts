export type RoleCategory = 'leader' | 'follower' | 'unreachable'

// Redis reports "master"/"slave", Mongo reports "primary"/"secondary" - normalized to one small set
// so ServiceNode can style either vocabulary the same way instead of hardcoding both.
export function categorizeRole(role: string): RoleCategory {
  if (role === 'master' || role === 'primary') return 'leader'
  if (role === 'replica' || role === 'secondary') return 'follower'
  return 'unreachable'
}
