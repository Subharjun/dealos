import { BrowserRouter, NavLink, Route, Routes } from "react-router-dom";
import { SignIn } from "./components";
import { SessionProvider, useSession } from "./session";
import Catalog from "./pages/Catalog";
import Listing from "./pages/Listing";
import Company from "./pages/Company";
import Selling, { MyListing, NewListing } from "./pages/Selling";
import Buying, { MyRfq, NewRfq } from "./pages/Buying";
import Deals from "./pages/Deals";
import Chat from "./pages/Chat";

function TopBar() {
  const { user, company, isBuyer, isSeller } = useSession();
  return (
    <header className="topbar">
      <div className="topbar-inner">
        <NavLink to="/" className="brand"><span className="brand-mark" aria-hidden />DealOS</NavLink>
        <nav className="nav">
          <NavLink to="/" end>Catalog</NavLink>
          {user && (isBuyer || !company) ? <NavLink to="/buy">Buying</NavLink> : null}
          {user && isSeller ? <NavLink to="/sell">Selling</NavLink> : null}
          {user && company ? <NavLink to="/deals">Deals</NavLink> : null}
          {user && company ? <NavLink to="/chat">Assistant</NavLink> : null}
          {user ? <NavLink to="/company">{company ? "Company" : "Set up company"}</NavLink> : null}
        </nav>
        <div className="who">
          {user ? (
            <>
              <span className="small muted">{user.firstName || user.userName}</span>
              <a className="button" href="/Account/Login/LogOff?returnUrl=%2F">Sign out</a>
            </>
          ) : <SignIn />}
        </div>
      </div>
    </header>
  );
}

export default function App() {
  return (
    <BrowserRouter>
      <SessionProvider>
        <TopBar />
        <main>
          <Routes>
            <Route path="/" element={<Catalog />} />
            <Route path="/listing/:id" element={<Listing />} />
            <Route path="/company" element={<Company />} />
            <Route path="/sell" element={<Selling />} />
            <Route path="/sell/new" element={<NewListing />} />
            <Route path="/sell/:id" element={<MyListing />} />
            <Route path="/buy" element={<Buying />} />
            <Route path="/buy/new" element={<NewRfq />} />
            <Route path="/buy/:id" element={<MyRfq />} />
            <Route path="/deals" element={<Deals />} />
            <Route path="/chat" element={<Chat />} />
            <Route path="*" element={<Catalog />} />
          </Routes>
        </main>
      </SessionProvider>
    </BrowserRouter>
  );
}
