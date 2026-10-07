import { useCallback, useEffect, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { B, create, list } from "../api";
import { RequireCompany, Status } from "../components";
import { useSession } from "../session";

interface Message { gc_messageid: string; gc_text: string | null; gc_isplatform: boolean | null; gc_senderlabel: string | null; gc_attachments: string | null; createdon: string }
interface Conversation { gc_conversationid: string }

const ASSISTANTS = {
  buyer: { value: B, name: "Buyer Concierge", intro: "I can search the catalog, explain the evidence on a listing, show your RFQs, deals and offers, and draft an RFQ for you to send." },
  seller: { value: B + 1, name: "Seller Assistant", intro: "I can show what your listings still need for verification, record your answers to the verification team's questions, start a listing, and explain invites, deals and payouts." },
};

export default function Chat() {
  return <RequireCompany><ChatRoom /></RequireCompany>;
}

function ChatRoom() {
  const { user, isSeller, isBuyer } = useSession();
  const [params, setParams] = useSearchParams();
  const side: "buyer" | "seller" = params.get("assistant") === "seller" || (!params.get("assistant") && isSeller && !isBuyer) ? "seller" : "buyer";
  const assistant = ASSISTANTS[side];
  const about = params.get("about");
  const [conversation, setConversation] = useState<string | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [text, setText] = useState(about ? `About “${about}”: ` : "");
  const [waitingSince, setWaitingSince] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const log = useRef<HTMLDivElement>(null);

  // The latest conversation with this assistant (the guard ties conversations to the signed-in contact).
  useEffect(() => {
    setConversation(null);
    setMessages([]);
    list<Conversation>(`gc_conversations?$select=gc_conversationid&$filter=gc_assistant eq ${assistant.value}&$orderby=createdon desc&$top=1`)
      .then((rows) => setConversation(rows[0]?.gc_conversationid ?? null))
      .catch((e: Error) => setError(e.message));
  }, [assistant.value]);

  const load = useCallback(async (id: string) => {
    const rows = await list<Message>(`gc_messages?$select=gc_messageid,gc_text,gc_isplatform,gc_senderlabel,gc_attachments,createdon&$filter=_gc_conversation_value eq ${id}&$orderby=createdon asc&$top=200`);
    setMessages(rows);
    return rows;
  }, []);

  useEffect(() => {
    if (conversation) void load(conversation).catch((e: Error) => setError(e.message));
  }, [conversation, load]);

  // Poll quickly while a reply is due, slowly otherwise.
  useEffect(() => {
    if (!conversation) return;
    const ms = waitingSince ? 3000 : 20000;
    const timer = setInterval(async () => {
      try {
        const rows = await load(conversation);
        const last = rows[rows.length - 1];
        if (waitingSince && last?.gc_isplatform) setWaitingSince(null);
        if (waitingSince && Date.now() - waitingSince > 150000) {
          setWaitingSince(null);
          setError("No reply yet. The assistant may be busy; your message is saved and the team can see it.");
        }
      } catch {
        /* keep polling */
      }
    }, ms);
    return () => clearInterval(timer);
  }, [conversation, waitingSince, load]);

  useEffect(() => {
    log.current?.scrollTo({ top: log.current.scrollHeight, behavior: "smooth" });
  }, [messages, waitingSince]);

  const send = async (body: string) => {
    const message = body.trim();
    if (!message) return;
    setError(null);
    try {
      let id = conversation;
      if (!id) {
        id = await create("gc_conversations", { gc_name: `${assistant.name} chat`, gc_assistant: assistant.value,
          "gc_Contact@odata.bind": `/contacts(${user!.contactId})` });
        setConversation(id);
      }
      await create("gc_messages", { gc_name: message.slice(0, 100), gc_text: message, "gc_conversation@odata.bind": `/gc_conversations(${id})` });
      setText("");
      setWaitingSince(Date.now());
      await load(id);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const suggestions = (m: Message): string[] => {
    try { return (JSON.parse(m.gc_attachments ?? "{}").next_steps as string[]) ?? []; } catch { return []; }
  };
  const last = messages[messages.length - 1];

  return (
    <div className="stack" style={{ maxWidth: 820 }}>
      <div className="page-head">
        <div><h1>{assistant.name}</h1><p className="muted">{assistant.intro}</p></div>
        {isBuyer && isSeller ? (
          <div className="row">
            <button className={side === "buyer" ? "primary" : ""} onClick={() => setParams({ assistant: "buyer" })}>Buying</button>
            <button className={side === "seller" ? "primary" : ""} onClick={() => setParams({ assistant: "seller" })}>Selling</button>
          </div>
        ) : null}
      </div>
      <div className="card chat">
        <div className="chat-log" ref={log} aria-live="polite">
          {messages.length === 0 ? <div className="bubble bot"><span className="who">{assistant.name}</span>Hello. {assistant.intro} What do you need?</div> : null}
          {messages.map((m) => (
            <div key={m.gc_messageid} className={`bubble ${m.gc_isplatform ? "bot" : "me"}`}>
              {m.gc_isplatform ? <span className="who">{m.gc_senderlabel ?? "DealOS"}</span> : null}
              {m.gc_text}
            </div>
          ))}
          {waitingSince ? <div className="typing">{assistant.name} is checking…</div> : null}
          {last?.gc_isplatform && !waitingSince && suggestions(last).length > 0 ? (
            <div className="suggest">{suggestions(last).map((s) => <button key={s} onClick={() => void send(s)}>{s}</button>)}</div>
          ) : null}
        </div>
        <form className="chat-form" onSubmit={(e) => { e.preventDefault(); void send(text); }}>
          <textarea value={text} onChange={(e) => setText(e.target.value)} placeholder="Type your message"
            onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); void send(text); } }} />
          <button className="primary" type="submit" disabled={!text.trim() || !!waitingSince}>Send</button>
        </form>
      </div>
      <Status error={error} />
      <p className="muted small">The assistant can't accept offers, send RFQs or move money; anything it prepares is a draft you finish yourself. Conversations are stored with your company's records.</p>
    </div>
  );
}
