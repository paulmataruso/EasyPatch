import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { KeyRound } from 'lucide-react';
import { api, request } from '../../api/client';
import type { CurrentUserDto, LoginResponse, MfaSetupResponse, RecoveryCodesResponse } from '../../api/types';
import { useAuth } from '../../auth/AuthContext';
import { AuthShell } from '../../components/Layout';
import { Alert, Button, buttonClasses, cx, InlineError, Spinner, TextField } from '../../components/ui';
import { CopyButton, DownloadButton, OneTimeNotice } from '../../components/Copy';

export const PASSWORD_HINT = 'At least 12 characters. Must not contain your username. Passphrases are encouraged.';

/** Only same-site absolute paths are accepted as return targets (prevents open redirects). */
export function safeReturnTo(value: string | null): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\') || value.startsWith('/login')) return '/';
  return value;
}

export function LoginPage() {
  const { setUser, refresh } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = safeReturnTo(params.get('returnTo'));
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = await request<LoginResponse>('POST', '/auth/login', { body: { email: email.trim(), password }, silentAuth: true });
      setPassword('');
      setUser(result.user);
      await refresh();
      const query = returnTo !== '/' ? `?returnTo=${encodeURIComponent(returnTo)}` : '';
      if (result.mfaRequired) navigate(`/login/mfa${query}`, { replace: true });
      else if (result.mfaSetupRequired) navigate('/account/mfa-setup', { replace: true });
      else if (result.user.mustChangePassword) navigate('/account/change-password', { replace: true });
      else navigate(returnTo, { replace: true });
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthShell title="Sign in" subtitle="EasyPatch administrator console">
      <form onSubmit={submit} className="flex flex-col gap-3.5" noValidate>
        <TextField
          label="Email address"
          name="email"
          type="email"
          autoComplete="username"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
          autoFocus
        />
        <TextField
          label="Password"
          name="password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        <InlineError error={error} />
        <Button type="submit" variant="primary" loading={busy} disabled={!email.trim() || !password} className="mt-1 w-full">
          Sign in
        </Button>
        <p className="text-center text-xs text-muted">Forgot your password? Ask a super administrator for a reset link.</p>
      </form>
    </AuthShell>
  );
}

export function MfaVerifyPage() {
  const { refresh, setUser } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = safeReturnTo(params.get('returnTo'));
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [useRecovery, setUseRecovery] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await request('POST', '/auth/mfa/verify', { body: { code: code.trim() }, silentAuth: true });
      await refresh();
      navigate(returnTo, { replace: true });
    } catch (err) {
      setError(err);
      setCode('');
    } finally {
      setBusy(false);
    }
  }

  async function cancel() {
    try {
      await api.post('/auth/logout');
    } catch {
      /* ignore */
    }
    setUser(null);
    navigate('/login', { replace: true });
  }

  return (
    <AuthShell
      title="Two-factor verification"
      subtitle={useRecovery ? 'Enter one of your unused recovery codes.' : 'Enter the 6-digit code from your authenticator app.'}
    >
      <form onSubmit={submit} className="flex flex-col gap-3.5">
        <TextField
          label={useRecovery ? 'Recovery code' : 'Verification code'}
          name="code"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          autoComplete="one-time-code"
          inputMode={useRecovery ? 'text' : 'numeric'}
          pattern={useRecovery ? undefined : '[0-9 ]*'}
          maxLength={useRecovery ? 32 : 7}
          autoFocus
          required
        />
        <InlineError error={error} />
        <Button type="submit" variant="primary" loading={busy} disabled={code.trim().length < 6} className="w-full">
          Verify
        </Button>
        <div className="flex items-center justify-between text-xs">
          <button type="button" className="text-accent hover:underline" onClick={() => { setUseRecovery((v) => !v); setCode(''); }}>
            {useRecovery ? 'Use authenticator code' : 'Use a recovery code'}
          </button>
          <button type="button" className="text-muted hover:text-fg" onClick={cancel}>
            Sign in as someone else
          </button>
        </div>
      </form>
    </AuthShell>
  );
}

/** Two-step MFA enrollment: scan QR → confirm code → show recovery codes once. */
/** `onEnabled` fires once MFA is active on the server, before the one-time recovery codes are acknowledged. */
export function MfaEnrollment({ onDone, onEnabled }: { onDone: () => void; onEnabled?: () => void }) {
  const [setup, setSetup] = useState<MfaSetupResponse | null>(null);
  const [setupError, setSetupError] = useState<unknown>(null);
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    api
      .post<MfaSetupResponse>('/auth/mfa/setup')
      .then(setSetup)
      .catch(setSetupError);
  }, []);

  async function enable(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = await api.post<RecoveryCodesResponse>('/auth/mfa/enable', { code: code.trim() });
      setCodes(result.recoveryCodes);
      onEnabled?.();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  if (codes) {
    return <RecoveryCodesView codes={codes} onDone={onDone} />;
  }
  if (setupError) return <InlineError error={setupError} />;
  if (!setup) return <Spinner label="Preparing authenticator setup…" />;

  const grouped = setup.secret.replace(/(.{4})/g, '$1 ').trim();
  return (
    <form onSubmit={enable} className="flex flex-col gap-4">
      <ol className="list-decimal space-y-1 pl-5 text-sm text-fg">
        <li>Open an authenticator app (Microsoft Authenticator, Google Authenticator, 1Password, Bitwarden…).</li>
        <li>Scan the QR code, or enter the secret manually.</li>
        <li>Enter the 6-digit code the app shows.</li>
      </ol>
      <div className="flex flex-col items-center gap-3">
        <img src={setup.qrCodeDataUri} alt="QR code for your authenticator app" width={192} height={192} className="rounded-md bg-white p-2" />
        <div className="w-full text-center">
          <p className="text-xs text-muted">Secret key</p>
          <p className="font-mono text-sm tracking-wider break-all select-all">{grouped}</p>
          <CopyButton text={setup.secret} label="Copy secret" className="mt-1.5" />
        </div>
      </div>
      <TextField
        label="Verification code"
        value={code}
        onChange={(e) => setCode(e.target.value)}
        autoComplete="one-time-code"
        inputMode="numeric"
        maxLength={7}
        required
      />
      <InlineError error={error} />
      <Button type="submit" variant="primary" loading={busy} disabled={code.trim().length < 6}>
        Enable two-factor authentication
      </Button>
    </form>
  );
}

export function RecoveryCodesView({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  const [saved, setSaved] = useState(false);
  const text = codes.join('\n');
  return (
    <div className="flex flex-col gap-3.5">
      <OneTimeNotice>
        Each recovery code signs you in once if you lose your authenticator. Store them in your password manager.
      </OneTimeNotice>
      <ul className="grid grid-cols-2 gap-1.5 rounded-md border border-line bg-raised p-3 font-mono text-sm">
        {codes.map((c) => (
          <li key={c} className="select-all">
            {c}
          </li>
        ))}
      </ul>
      <div className="flex gap-2">
        <CopyButton text={text} label="Copy codes" />
        <DownloadButton fileName="easypatch-recovery-codes.txt" text={`EasyPatch recovery codes\n\n${text}\n`} />
      </div>
      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={saved} onChange={(e) => setSaved(e.target.checked)} className="size-4" />
        I have saved these recovery codes
      </label>
      <Button variant="primary" disabled={!saved} onClick={onDone}>
        Continue
      </Button>
    </div>
  );
}

export function MfaSetupRequiredPage() {
  const { refresh, setUser } = useAuth();
  const navigate = useNavigate();
  return (
    <AuthShell title="Set up two-factor authentication" subtitle="Your organization requires two-factor authentication for every administrator account.">
      <MfaEnrollment
        onDone={async () => {
          await refresh();
          navigate('/', { replace: true });
        }}
      />
      <button
        type="button"
        className="mt-4 w-full text-center text-xs text-muted hover:text-fg"
        onClick={async () => {
          try {
            await api.post('/auth/logout');
          } catch {
            /* ignore */
          }
          setUser(null);
          navigate('/login', { replace: true });
        }}
      >
        Sign out
      </button>
    </AuthShell>
  );
}

export function PasswordChangeForm({ onDone, user }: { onDone: () => void; user: CurrentUserDto | null }) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const localError = passwordProblem(next, confirm, user?.username);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (localError) return;
    setBusy(true);
    setError(null);
    try {
      await api.post('/auth/password/change', { currentPassword: current, newPassword: next });
      setCurrent('');
      setNext('');
      setConfirm('');
      onDone();
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-3.5">
      <TextField label="Current password" type="password" autoComplete="current-password" value={current} onChange={(e) => setCurrent(e.target.value)} required />
      <TextField label="New password" type="password" autoComplete="new-password" value={next} onChange={(e) => setNext(e.target.value)} hint={PASSWORD_HINT} required />
      <TextField
        label="Confirm new password"
        type="password"
        autoComplete="new-password"
        value={confirm}
        onChange={(e) => setConfirm(e.target.value)}
        error={confirm && next !== confirm ? 'Passwords do not match.' : null}
        required
      />
      <InlineError error={error} />
      <Button type="submit" variant="primary" loading={busy} disabled={!current || !!localError}>
        Change password
      </Button>
    </form>
  );
}

/** Client-side mirror of the server password policy, for immediate feedback only (the server enforces it). */
export function passwordProblem(password: string, confirm: string, username?: string | null): string | null {
  if (password.length < 12) return 'Use at least 12 characters.';
  if (password.length > 256) return 'Use at most 256 characters.';
  if (username && password.toLowerCase().includes(username.toLowerCase())) return 'The password must not contain the username.';
  if (new Set(password).size < 4) return 'Use at least 4 different characters.';
  if (password !== confirm) return 'Passwords do not match.';
  return null;
}

export function PasswordChangeRequiredPage() {
  const { refresh, user } = useAuth();
  const navigate = useNavigate();
  return (
    <AuthShell title="Change your password" subtitle="An administrator requires you to choose a new password before continuing.">
      <PasswordChangeForm
        user={user}
        onDone={async () => {
          await refresh();
          navigate('/', { replace: true });
        }}
      />
    </AuthShell>
  );
}

export function ResetPasswordPage() {
  // The token travels in the URL fragment so it never reaches server logs or Referer headers.
  const [token] = useState(() => {
    const hash = window.location.hash.startsWith('#') ? window.location.hash.slice(1) : '';
    return new URLSearchParams(hash).get('token') ?? '';
  });
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState(false);

  useEffect(() => {
    // Remove the token from the address bar and history.
    if (window.location.hash) window.history.replaceState(null, '', window.location.pathname);
  }, []);

  const localError = passwordProblem(password, confirm);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (localError) return;
    setBusy(true);
    setError(null);
    try {
      await request('POST', '/auth/password/reset', { body: { token, newPassword: password }, silentAuth: true });
      setDone(true);
      setPassword('');
      setConfirm('');
    } catch (err) {
      setError(err);
    } finally {
      setBusy(false);
    }
  }

  if (!token) {
    return (
      <AuthShell title="Reset password">
        <Alert tone="warn" title="This reset link is incomplete">
          Open the full link you received from your administrator. Reset links expire after 60 minutes and work once.
        </Alert>
        <Link to="/login" className="mt-4 block text-center text-sm text-accent hover:underline">
          Back to sign in
        </Link>
      </AuthShell>
    );
  }

  if (done) {
    return (
      <AuthShell title="Password updated">
        <Alert tone="ok">Your password has been set. Sign in with the new password.</Alert>
        <Link to="/login" className={cx(buttonClasses('primary'), 'mt-4 w-full')}>
          <KeyRound className="size-4" aria-hidden />
          Go to sign in
        </Link>
      </AuthShell>
    );
  }

  return (
    <AuthShell title="Choose a new password" subtitle="This link works once and expires 60 minutes after it was issued.">
      <form onSubmit={submit} className="flex flex-col gap-3.5">
        <TextField label="New password" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} hint={PASSWORD_HINT} required autoFocus />
        <TextField
          label="Confirm new password"
          type="password"
          autoComplete="new-password"
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
          error={confirm && password !== confirm ? 'Passwords do not match.' : null}
          required
        />
        <InlineError error={error} />
        <Button type="submit" variant="primary" loading={busy} disabled={!!localError}>
          Set password
        </Button>
      </form>
    </AuthShell>
  );
}
