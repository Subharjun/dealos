import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { B, get, portalUser, type PortalUser } from "./api";

export interface Company {
  accountid: string;
  name: string;
  gc_kybstatus: number | null;
  gc_trusttier: number | null;
  gc_partyrole: string | null; // multi-select: "303300000,303300001"
  gc_registrationnumber: string | null;
}

interface Session {
  user: PortalUser | null;
  company: Company | null;
  loading: boolean;
  isSeller: boolean;
  isBuyer: boolean;
  refresh: () => Promise<void>;
}

const SessionContext = createContext<Session>({ user: null, company: null, loading: true, isSeller: false, isBuyer: false, refresh: async () => {} });

export function SessionProvider({ children }: { children: ReactNode }) {
  const user = portalUser();
  const [company, setCompany] = useState<Company | null>(null);
  const [loading, setLoading] = useState(!!user);

  const refresh = useCallback(async () => {
    if (!user?.contactId) {
      setLoading(false);
      return;
    }
    try {
      const me = await get<{ _parentcustomerid_value: string | null }>(`contacts(${user.contactId})?$select=_parentcustomerid_value`);
      if (me._parentcustomerid_value) {
        setCompany(await get<Company>(`accounts(${me._parentcustomerid_value})?$select=accountid,name,gc_kybstatus,gc_trusttier,gc_partyrole,gc_registrationnumber`));
      } else {
        setCompany(null);
      }
    } catch {
      setCompany(null);
    } finally {
      setLoading(false);
    }
  }, [user?.contactId]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const roles = (company?.gc_partyrole ?? "").split(",").map((r) => Number(r));
  return (
    <SessionContext.Provider value={{ user, company, loading, isSeller: roles.includes(B) || roles.includes(B + 2), isBuyer: roles.includes(B + 1) || roles.includes(B + 2), refresh }}>
      {children}
    </SessionContext.Provider>
  );
}

export function useSession(): Session {
  return useContext(SessionContext);
}
