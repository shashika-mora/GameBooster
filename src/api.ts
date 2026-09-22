import { invoke } from '@tauri-apps/api/core';

export interface Game { id: number; name: string; executable: string | null; installPath: string | null; source: string }
export interface Profile { gameId: number; preset: 'Balanced' | 'Performance' | 'Custom'; powerScheme: string | null }
export interface Session { id: number; gameId: number; gameName: string; profilePreset: string; startedAt: string; endedAt: string | null; status: string; restorationResult: string | null }
export interface Recovery { sessionId: number; gameName: string; previousScheme: string | null; appliedScheme: string | null; status: string }
export interface MonitorSample { cpuPercent: number; usedMemoryBytes: number; totalMemoryBytes: number }

export const api = {
  games: () => invoke<Game[]>('list_games'),
  addGame: (name: string, executable: string) => invoke<number>('add_game', { name, executable }),
  setExecutable: (gameId: number, executable: string) => invoke<void>('set_game_executable', { gameId, executable }),
  scanSteam: () => invoke<number>('scan_steam'),
  profile: (gameId: number) => invoke<Profile>('get_profile', { gameId }),
  saveProfile: (profile: Profile) => invoke<void>('save_profile', { profile }),
  sessions: () => invoke<Session[]>('list_sessions'),
  recovery: () => invoke<Recovery | null>('recovery'),
  sessionActive: () => invoke<boolean>('session_active'),
  recover: () => invoke<string>('recover_session'),
  powerScheme: () => invoke<string>('active_power_scheme'),
  sample: () => invoke<MonitorSample>('monitor_sample'),
  launch: (gameId: number) => invoke<number>('launch_game', { gameId }),
};
