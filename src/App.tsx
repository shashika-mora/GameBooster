import { useCallback, useEffect, useState } from 'react';
import { api, type Game, type MonitorSample, type Profile, type Recovery, type Session } from './api';

type Page = 'Dashboard' | 'Games' | 'Profiles' | 'Sessions' | 'System Monitor' | 'Settings';
const pages: Page[] = ['Dashboard', 'Games', 'Profiles', 'Sessions', 'System Monitor', 'Settings'];
const emptySample: MonitorSample = { cpuPercent: 0, usedMemoryBytes: 0, totalMemoryBytes: 0 };

function formatBytes(value: number) { return `${(value / 1024 ** 3).toFixed(1)} GB`; }
function time(value: string) { return new Date(value).toLocaleString(); }
function errorText(value: unknown) { return value instanceof Error ? value.message : String(value); }

export default function App() {
  const [page, setPage] = useState<Page>('Dashboard');
  const [games, setGames] = useState<Game[]>([]);
  const [sessions, setSessions] = useState<Session[]>([]);
  const [recovery, setRecovery] = useState<Recovery | null>(null);
  const [running, setRunning] = useState(false);
  const [sample, setSample] = useState<MonitorSample>(emptySample);
  const [selected, setSelected] = useState<number | null>(null);
  const [profile, setProfile] = useState<Profile | null>(null);
  const [scheme, setScheme] = useState<string>('');
  const [name, setName] = useState('');
  const [executable, setExecutable] = useState('');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const [nextGames, nextSessions, nextRecovery, nextRunning] = await Promise.all([api.games(), api.sessions(), api.recovery(), api.sessionActive()]);
      setGames(nextGames); setSessions(nextSessions); setRecovery(nextRecovery); setRunning(nextRunning);
      setSelected(current => current ?? nextGames[0]?.id ?? null);
    } catch (e) { setMessage(errorText(e)); }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    const timer = window.setInterval(() => { void refresh(); }, 3000);
    return () => window.clearInterval(timer);
  }, [refresh]);
  useEffect(() => {
    const getSample = async () => { try { setSample(await api.sample()); } catch { /* unavailable during startup */ } };
    void getSample();
    const timer = window.setInterval(() => { void getSample(); }, 3000);
    return () => window.clearInterval(timer);
  }, []);
  useEffect(() => {
    if (selected === null) { setProfile(null); return; }
    setProfile(null);
    void api.profile(selected).then(setProfile).catch(e => setMessage(errorText(e)));
  }, [selected]);
  useEffect(() => { void api.powerScheme().then(setScheme).catch(() => setScheme('Unavailable')); }, []);

  const run = async (action: () => Promise<unknown>, success: string) => {
    setBusy(true); setMessage('');
    try { await action(); setMessage(success); await refresh(); }
    catch (e) { setMessage(errorText(e)); }
    finally { setBusy(false); }
  };

  const current = games.find(game => game.id === selected);
  const metrics = <div className="metrics"><div className="metric"><span>CPU</span><strong>{sample.cpuPercent.toFixed(0)}%</strong><div className="bar"><i style={{ width: `${Math.min(sample.cpuPercent, 100)}%` }} /></div></div><div className="metric"><span>Memory</span><strong>{formatBytes(sample.usedMemoryBytes)} <small>/ {formatBytes(sample.totalMemoryBytes)}</small></strong><div className="bar"><i style={{ width: `${sample.totalMemoryBytes ? 100 * sample.usedMemoryBytes / sample.totalMemoryBytes : 0}%` }} /></div></div><div className="metric"><span>GPU</span><strong>Unavailable</strong><p>Reliable GPU sampling is planned.</p></div></div>;

  return <div className="app"><aside className="sidebar"><div className="brand"><div className="brand-mark">G</div><div><strong>GameBooster</strong><small>SESSION CONTROL</small></div></div><nav>{pages.map((item, index) => <button key={item} className={page === item ? 'active' : ''} onClick={() => setPage(item)}><span>{['◫','▦','◈','◷','◉','⚙'][index]}</span>{item}</button>)}</nav><div className="side-foot"><span className="status-dot" /> Offline by design <small>v0.1.0</small></div></aside>
    <main className="main"><header><div><span className="eyebrow">GAME SESSION MANAGEMENT</span><h1>{page}</h1></div><div className="header-state"><span className={running ? 'pulse' : 'status-dot'} />{running ? 'Session running' : recovery ? 'Recovery needed' : 'System ready'}</div></header>
    {message && <div className="notice" role="status">{message}<button onClick={() => setMessage('')}>×</button></div>}
    {recovery && <div className="recovery"><div><strong>Unfinished session: {recovery.gameName}</strong><p>A previous session has not been fully restored. Review the current Windows power plan and recover before launching another game.</p></div><button className="primary" disabled={busy || running} onClick={() => void run(api.recover, 'Recovery completed')}>Restore saved state</button></div>}
    {page === 'Dashboard' && <><section className="hero"><div><span className="eyebrow">READY WHEN YOU ARE</span><h2>Play with a plan.<br /><em>Return to normal.</em></h2><p>Choose a game, review its profile, and launch. GameBooster records any power-plan change and restores the original plan after the process exits.</p><button className="primary" onClick={() => setPage('Games')}>Open game library →</button></div><div className="hero-stat"><span>CURRENT STATE</span><strong>{recovery ? 'Recovery needed' : running ? 'In session' : 'Standby'}</strong><small>{games.length} games in your library</small></div></section><h3>System at a glance</h3>{metrics}<div className="split"><section className="panel"><div className="panel-head"><h3>Your games</h3><button className="text-button" onClick={() => setPage('Games')}>View all →</button></div>{games.slice(0, 4).map(g => <div className="row" key={g.id}><div className="game-icon">{g.name[0]}</div><div><strong>{g.name}</strong><small>{g.source === 'steam' ? 'Steam' : 'Manual'}</small></div><button onClick={() => { setSelected(g.id); setPage('Games'); }}>Open</button></div>)}{games.length === 0 && <p className="muted">Add a game or scan Steam to get started.</p>}</section><section className="panel"><div className="panel-head"><h3>Recent sessions</h3><button className="text-button" onClick={() => setPage('Sessions')}>History →</button></div>{sessions.slice(0, 4).map(s => <div className="row" key={s.id}><div className="game-icon muted-icon">◷</div><div><strong>{s.gameName}</strong><small>{time(s.startedAt)}</small></div><span className="tag">{s.status}</span></div>)}{sessions.length === 0 && <p className="muted">No sessions yet.</p>}</section></div></>}
    {page === 'Games' && <div className="split"><section className="panel"><div className="panel-head"><h3>Library <span className="count">{games.length}</span></h3><button onClick={() => void run(api.scanSteam, 'Steam scan complete')} disabled={busy}>Scan Steam</button></div>{games.map(g => <button className={`game-row ${selected === g.id ? 'selected' : ''}`} key={g.id} onClick={() => setSelected(g.id)}><div className="game-icon">{g.name[0]}</div><div><strong>{g.name}</strong><small>{g.source === 'steam' ? 'Steam discovery' : 'Manually added'} · {g.executable ? 'Ready' : 'Needs executable'}</small></div><span>›</span></button>)}{games.length === 0 && <p className="muted">Your library is empty.</p>}<form className="add-form" onSubmit={e => { e.preventDefault(); void run(() => api.addGame(name, executable), 'Game added'); setName(''); setExecutable(''); }}><h4>Add an executable</h4><label>Game name<input value={name} onChange={e => setName(e.target.value)} placeholder="e.g. Portal 2" required /></label><label>Full .exe path<input value={executable} onChange={e => setExecutable(e.target.value)} placeholder="C:\Games\Example\game.exe" required /></label><button className="primary" disabled={busy}>Add game</button></form></section><section className="panel detail">{current ? <><div className="large-icon">{current.name[0]}</div><span className="eyebrow">{current.source.toUpperCase()} GAME</span><h2>{current.name}</h2><p className="muted path">{current.installPath ?? 'Local installation'}</p><div className="detail-line"><span>Executable</span><strong>{current.executable ?? 'Not set'}</strong></div><div className="detail-line"><span>Profile</span><strong>{profile?.preset ?? 'Balanced'}</strong></div>{!current.executable && <form className="add-form" onSubmit={e => { e.preventDefault(); void run(() => api.setExecutable(current.id, executable), 'Executable saved'); setExecutable(''); }}><label>Set executable path<input value={executable} onChange={e => setExecutable(e.target.value)} placeholder="C:\Games\Example\game.exe" required /></label><button disabled={busy}>Save path</button></form>}<div className="actions"><button className="primary" disabled={busy || !!recovery || !current.executable} onClick={() => void run(() => api.launch(current.id), 'Game launched; automatic restoration is armed')}>Launch game</button><button onClick={() => setPage('Profiles')}>Edit profile</button></div><p className="hint">The app monitors the launched executable. Games that hand off to another launcher may end the session early.</p></> : <p className="muted">Select a game to view details.</p>}</section></div>}
    {page === 'Profiles' && <section className="panel narrow"><div className="panel-head"><h3>Per-game profile</h3><select value={selected ?? ''} onChange={e => setSelected(Number(e.target.value))}>{games.map(g => <option value={g.id} key={g.id}>{g.name}</option>)}</select></div>{profile ? <><p className="muted">Balanced makes no system change. Performance selects Windows High performance. Custom uses the scheme GUID you specify.</p><label>Preset<select value={profile.preset} onChange={e => { const preset = e.target.value as Profile['preset']; setProfile({ ...profile, preset, powerScheme: preset === 'Balanced' ? null : preset === 'Performance' ? '8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c' : profile.powerScheme }); }}><option>Balanced</option><option>Performance</option><option>Custom</option></select></label><label>Windows power scheme GUID<input value={profile.powerScheme ?? ''} onChange={e => setProfile({ ...profile, powerScheme: e.target.value || null })} placeholder="Leave blank for no change" disabled={profile.preset !== 'Custom'} /></label><p className="hint">The chosen plan must already exist in Windows. If Windows denies the change, the game will not launch and the saved state can be recovered.</p><button className="primary" disabled={busy} onClick={() => void run(() => api.saveProfile(profile), 'Profile saved')}>Save profile</button></> : <p className="muted">Add a game to create a profile.</p>}</section>}
    {page === 'Sessions' && <section className="panel"><h3>Session history</h3><div className="table-head"><span>Game / profile</span><span>Started</span><span>Status</span><span>Restoration</span></div>{sessions.map(s => <div className="table-row" key={s.id}><strong>{s.gameName}<small>{s.profilePreset}</small></strong><span>{time(s.startedAt)}</span><span className="tag">{s.status}</span><span>{s.restorationResult ?? '—'}</span></div>)}{sessions.length === 0 && <p className="muted">No recorded sessions.</p>}</section>}
    {page === 'System Monitor' && <><section className="panel"><div className="panel-head"><h3>Local system monitor</h3><span className="tag">3 second sample</span></div>{metrics}<p className="hint">CPU and RAM come from local system APIs. GPU, FPS, and temperature remain unavailable until they can be measured reliably.</p></section></>}
    {page === 'Settings' && <section className="panel narrow"><h3>Settings & safety</h3><div className="detail-line"><span>Current Windows power scheme</span><strong>{scheme}</strong></div><button onClick={() => void api.powerScheme().then(setScheme).catch(e => setMessage(errorText(e)))}>Refresh scheme</button><div className="divider" /><h4>Privacy</h4><p className="muted">All library, profile, and session data is stored locally. There are no accounts, analytics, or network requirements.</p><h4>Recovery</h4><p className="muted">An unfinished session appears as a warning on startup. Restore the saved power plan before launching another game.</p></section>}
    </main></div>;
}
