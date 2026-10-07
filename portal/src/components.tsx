import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { antiForgeryToken, portalTenant } from "./api";
import { useSession } from "./session";

/** Loads data once (and on reload()), exposing loading and error state. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): { data: T | null; error: string | null; loading: boolean; reload: () => void } {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    let live = true;
    setLoading(true);
    load()
      .then((d) => live && (setData(d), setError(null)))
      .catch((e: Error) => live && setError(e.message))
      .finally(() => live && setLoading(false));
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);
  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { data, error, loading, reload };
}

/** Runs a write and reports its outcome. */
export function useAction(): { busy: boolean; error: string | null; done: string | null; run: (fn: () => Promise<unknown>, success?: string) => Promise<boolean> } {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const run = useCallback(async (fn: () => Promise<unknown>, success?: string) => {
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      await fn();
      if (success) setDone(success);
      return true;
    } catch (e) {
      setError((e as Error).message);
      return false;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, done, run };
}

export function Status({ error, done }: { error?: string | null; done?: string | null }) {
  if (error) return <div className="notice error" role="alert">{error}</div>;
  if (done) return <div className="notice ok" role="status">{done}</div>;
  return null;
}

export function Loading({ what = "Loading" }: { what?: string }) {
  return <div className="empty">{what}…</div>;
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

const STATUS_CLASS: Record<string, string> = { Verified: "verified", Documented: "documented", Claimed: "claimed" };

/** Evidence status of one value. */
export function Evidence({ status }: { status: string | null | undefined }) {
  const cls = STATUS_CLASS[status ?? ""] ?? "none";
  const text = status === "Documented" ? "Documented" : status === "Claimed" ? "Seller states" : status ?? "Not shown";
  return <span className={`pill ${cls}`} title={EVIDENCE_HELP[status ?? ""] ?? ""}>{text}</span>;
}

export const EVIDENCE_HELP: Record<string, string> = {
  Verified: "Independently confirmed: our inspection, the issuer or an official registry.",
  Documented: "Supported by a document the seller uploaded (for example an SGS certificate of analysis).",
  Claimed: "Stated by the seller only; not yet backed by a document.",
};

/** Listing badge. */
export function Badge({ badge }: { badge: string | null | undefined }) {
  if (badge === "Verified") return <span className="pill verified" title="Every critical value is independently verified, including physical inspection.">✓ Verified listing</span>;
  if (badge === "Documented") return <span className="pill documented" title="Every critical value is supported by a document.">Documented listing</span>;
  return <span className="pill none" title="Some critical values are only stated by the seller.">Not yet documented</span>;
}

export function SignIn({ label = "Sign in" }: { label?: string }) {
  const [token, setToken] = useState("");
  useEffect(() => {
    void antiForgeryToken().then(setToken);
  }, []);
  return (
    <form action={`/Account/Login/ExternalLogin?ReturnUrl=${encodeURIComponent(window.location.pathname)}`} method="post" style={{ display: "inline" }}>
      <input name="__RequestVerificationToken" type="hidden" value={token} />
      <button className="primary" name="provider" type="submit" value={`https://login.windows.net/${portalTenant()}/`}>{label}</button>
    </form>
  );
}

/** Children only for a signed-in user whose company profile exists. */
export function RequireCompany({ children, role }: { children: ReactNode; role?: "buyer" | "seller" }) {
  const { user, company, loading, isBuyer, isSeller } = useSession();
  if (!user) {
    return (
      <div className="card empty stack" style={{ alignItems: "center" }}>
        <h2>Sign in to continue</h2>
        <p className="muted">DealOS is a verified marketplace: buyers and sellers sign in and pass company checks before trading.</p>
        <SignIn />
      </div>
    );
  }
  if (loading) return <Loading />;
  if (!company) {
    return (
      <div className="card empty stack" style={{ alignItems: "center" }}>
        <h2>Set up your company first</h2>
        <p className="muted">Tell us about your company so we can start the company checks (KYB).</p>
        <Link className="button primary" to="/company">Set up company</Link>
      </div>
    );
  }
  if (role === "buyer" && !isBuyer) return <Empty>Your company is not registered as a buyer. Add the Buyer role on <Link to="/company">Company</Link>.</Empty>;
  if (role === "seller" && !isSeller) return <Empty>Your company is not registered as a seller. Add the Seller role on <Link to="/company">Company</Link>.</Empty>;
  return <>{children}</>;
}
