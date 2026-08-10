import { useState } from "react";
import { Button, Icon, InlineAlert, InputField, Modal, StatusChip } from "@mersal/design-system";
import type { IdentityUser, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useWrite } from "../api/useWrite";
import { issuerRoleFor } from "../config";
import { PORTALS, ZONES } from "../portals/catalog";
import { useLoc } from "./_shared";

/**
 * Phase 28.8 — creating and maintaining the people who use the platform.
 *
 * ==========================================================================================================
 * WHY THIS IS NEW WHEN THE ENDPOINTS ARE NOT
 * ==========================================================================================================
 * identity-service has had create / set-roles / deactivate / issue-reset since 17.4, and nothing in the SPA
 * has ever called any of them. The admin console could DISPLAY a user and could not bring one into
 * existence, correct one, re-enable one, or help one back in — so the only supported way to put somebody on
 * the platform was a database seed, and the only way to fix a mistyped address was to abandon the account.
 *
 * ==========================================================================================================
 * WHAT THIS SCREEN REFUSES TO DO
 * ==========================================================================================================
 * Choose a password. The administrator names a person and ticks the portals they need; the server generates
 * a credential nobody sees and emails a link. 28.7 removed the admin's power to SET a password on an
 * existing account on the grounds that there must be a moment at which only the owner knows it — and
 * creation is the one place that rule could most easily have been left behind.
 */

const S = {
  addUser: { en: "Add a user", ar: "إضافة مستخدم" },
  addUserHelp: {
    en: "You name the person and choose their portals. The password is set by them, from a link we email — nobody here ever sees it.",
    ar: "أنت تُدخل بيانات الشخص وتختار بواباته. أمّا كلمة المرور فيحددها هو عبر رابط نرسله بالبريد — ولا يراها أحد هنا.",
  },
  fullName: { en: "Full name", ar: "الاسم الكامل" },
  email: { en: "Email address", ar: "البريد الإلكتروني" },
  emailHelp: {
    en: "They sign in with this, and the invitation goes to it. It must not already belong to another account.",
    ar: "يسجّل الدخول به، وإليه تُرسل الدعوة. يجب ألّا يكون مستخدمًا في حساب آخر.",
  },
  username: { en: "Username", ar: "اسم المستخدم" },
  usernameHelp: {
    en: "Defaults to the email address. Change it only for an account with no mailbox of its own.",
    ar: "يُضبط تلقائيًا على البريد الإلكتروني. غيّره فقط لحساب بلا بريد خاص به.",
  },
  portals: { en: "Portals", ar: "البوابات" },
  portalsHelp: {
    en: "Each portal is a workspace with its own screens. Tick only what this person's job needs — they see nothing outside them.",
    ar: "كل بوابة مساحة عمل بشاشاتها. حدّد ما تحتاجه وظيفة هذا الشخص فقط — فلن يرى شيئًا خارجها.",
  },
  nameRequired: { en: "A full name is required.", ar: "الاسم الكامل مطلوب." },
  emailRequired: { en: "A valid email address is required.", ar: "يلزم بريد إلكتروني صحيح." },
  portalsRequired: {
    en: "Choose at least one portal — an account with none can sign in and reach nothing.",
    ar: "اختر بوابة واحدة على الأقل — الحساب بلا بوابة يسجّل الدخول ولا يصل إلى شيء.",
  },
  create: { en: "Create and invite", ar: "إنشاء وإرسال دعوة" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  createdInvited: {
    en: "Account created. An invitation to set a password has been emailed.",
    ar: "تم إنشاء الحساب. أُرسلت دعوة لتعيين كلمة المرور بالبريد الإلكتروني.",
  },
  createdNotInvited: {
    en: "Account created, but the invitation could not be sent. Use “Send reset link” on the row to try again — until then nobody can sign in to it.",
    ar: "تم إنشاء الحساب، لكن تعذّر إرسال الدعوة. استخدم «إرسال رابط إعادة التعيين» من الصف لإعادة المحاولة — وحتى ذلك الحين لا يمكن لأحد تسجيل الدخول إليه.",
  },
  emailTaken: { en: "Another account already uses this email address.", ar: "هناك حساب آخر يستخدم هذا البريد الإلكتروني." },

  editPortals: { en: "Change portals", ar: "تغيير البوابات" },
  editPortalsHelp: {
    en: "Removing a portal takes effect on their next sign-in, or within five minutes on this one.",
    ar: "إزالة بوابة تسري عند تسجيل الدخول التالي، أو خلال خمس دقائق على الجلسة الحالية.",
  },
  save: { en: "Save", ar: "حفظ" },

  sendReset: { en: "Send reset link", ar: "إرسال رابط إعادة التعيين" },
  sendResetTitle: { en: "Send a password reset link?", ar: "إرسال رابط إعادة تعيين كلمة المرور؟" },
  sendResetBody: {
    en: "A one-time link goes to their email address. It expires in 30 minutes and dies on first use. Their current password keeps working until they use it.",
    ar: "يُرسل رابط لمرة واحدة إلى بريدهم. ينتهي خلال 30 دقيقة ويُبطل بعد أول استخدام. تبقى كلمة مرورهم الحالية صالحة حتى يستخدموه.",
  },
  sendResetNoEmail: {
    en: "This account has no email address, so a link cannot be sent. Add one first.",
    ar: "لا يملك هذا الحساب بريدًا إلكترونيًا، لذا لا يمكن إرسال رابط. أضف بريدًا أولًا.",
  },
  resetSent: { en: "Reset link sent.", ar: "تم إرسال رابط إعادة التعيين." },

  deactivate: { en: "Deactivate", ar: "تعطيل" },
  deactivateTitle: { en: "Deactivate this account?", ar: "تعطيل هذا الحساب؟" },
  deactivateBody: {
    en: "They are signed out of every device immediately and cannot sign in again. Nothing is deleted — the record and its history remain, and the account can be brought back.",
    ar: "سيتم إخراجهم من كل الأجهزة فورًا ولن يتمكنوا من الدخول. لا يُحذف شيء — يبقى السجل وتاريخه، ويمكن إعادة تفعيل الحساب.",
  },
  reactivate: { en: "Reactivate", ar: "إعادة التفعيل" },
  reactivateTitle: { en: "Reactivate this account?", ar: "إعادة تفعيل هذا الحساب؟" },
  reactivateBody: {
    en: "They can sign in again with their existing password. Sessions ended by the deactivation are not restored.",
    ar: "سيتمكنون من الدخول مجددًا بكلمة مرورهم الحالية. الجلسات التي أُنهيت عند التعطيل لا تُستعاد.",
  },
  confirm: { en: "Confirm", ar: "تأكيد" },
} satisfies Record<string, Localized>;

/**
 * A shallow email check — one '@', something either side, a dot in the domain.
 *
 * Matching the server's rule deliberately, and for the server's reason: a regex claiming RFC 5322 rejects
 * real addresses (plus-tags, long TLDs) while still admitting undeliverable ones. The only proof an address
 * works is a message arriving at it, which is what the invitation is. This catches a typo before that.
 */
export function looksLikeEmail(value: string): boolean {
  const v = value.trim();
  if (!v || /\s/.test(v)) return false;
  const at = v.indexOf("@");
  if (at <= 0 || at !== v.lastIndexOf("@") || at === v.length - 1) return false;
  const domain = v.slice(at + 1);
  return domain.includes(".") && !domain.startsWith(".") && !domain.endsWith(".");
}

/**
 * The portal picker, grouped by the same three zones the sign-in picker uses.
 *
 * Shared shape on purpose: an administrator granting "Clinical & approvals" portals and a clinician
 * choosing between them should be looking at the same grouping, or the grant does not obviously correspond
 * to the thing the person will see.
 */
function PortalChecklist({
  chosen,
  onToggle,
}: {
  chosen: string[];
  onToggle: (role: string, on: boolean) => void;
}) {
  const t = useLoc();
  return (
    <div className="portal-checklist mrs-scroll">
      {ZONES.map((zone) => {
        const inZone = PORTALS.filter((p) => p.zone === zone.key);
        if (inZone.length === 0) return null;
        return (
          <fieldset key={zone.key} className="portal-checklist-zone">
            <legend>{t(zone.label)}</legend>
            {inZone.map((p) => (
              <label key={p.role} className="portal-checklist-item">
                <input
                  type="checkbox"
                  checked={chosen.includes(p.role)}
                  onChange={(e) => onToggle(p.role, e.currentTarget.checked)}
                />
                <span className="portal-checklist-name">{t(p.title)}</span>
                <span className="portal-checklist-eyebrow">{t(p.eyebrow)}</span>
              </label>
            ))}
          </fieldset>
        );
      })}
    </div>
  );
}

/** Create an account and invite it. */
export function CreateUserDialog({
  open,
  onClose,
  onCreated,
}: {
  open: boolean;
  onClose: () => void;
  onCreated: (result: { resetLinkSent: boolean }) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const [displayName, setDisplayName] = useState("");
  const [email, setEmail] = useState("");
  const [username, setUsername] = useState("");
  const [portals, setPortals] = useState<string[]>([]);
  const [touched, setTouched] = useState(false);

  const nameOk = displayName.trim().length > 0;
  const emailOk = looksLikeEmail(email);
  const portalsOk = portals.length > 0;

  async function submit() {
    setTouched(true);
    if (!nameOk || !emailOk || !portalsOk) return;
    const ok = await write.run(async () => {
      const result = await api.createIdentityUser({
        // The username defaults to the address rather than being derived from the name: a derived handle
        // collides the moment two people share a name, and the collision surfaces as a 409 the
        // administrator cannot act on because they did not choose the value.
        username: username.trim() || email.trim(),
        displayName: displayName.trim(),
        email: email.trim(),
        tenantId: "",
        // The ISSUER's names, not the portal keys. `lab` would be a 422; `lab_tech` is the grant.
        roles: portals.map((r) => issuerRoleFor(r as never)),
      });
      onCreated({ resetLinkSent: result.resetLinkSent });
    });
    if (ok) {
      setDisplayName("");
      setEmail("");
      setUsername("");
      setPortals([]);
      setTouched(false);
      onClose();
    }
  }

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={t(S.addUser)}
      description={t(S.addUserHelp)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t(S.cancel)}
          </Button>
          <Button variant="primary" leadingIcon={<Icon name="plus" />} loading={write.busy} onClick={() => void submit()}>
            {t(S.create)}
          </Button>
        </>
      }
    >
      <div className="stack-3">
        {write.error && (
          <InlineAlert tone="bad">
            {/* A 409 here is only ever the address: the username defaults to it, so the two conflicts have
                one cause and one remedy. Saying "conflict" would leave the administrator guessing which
                field to change. */}
            {write.error.status === 409 ? t(S.emailTaken) : t(write.error.message)}
          </InlineAlert>
        )}
        <InputField
          label={t(S.fullName)}
          value={displayName}
          error={touched && !nameOk ? t(S.nameRequired) : undefined}
          onChange={(e) => setDisplayName(e.target.value)}
        />
        <InputField
          label={t(S.email)}
          help={t(S.emailHelp)}
          type="email"
          autoComplete="off"
          value={email}
          error={touched && !emailOk ? t(S.emailRequired) : undefined}
          onChange={(e) => setEmail(e.target.value)}
        />
        <InputField
          label={t(S.username)}
          help={t(S.usernameHelp)}
          autoComplete="off"
          placeholder={email.trim()}
          value={username}
          onChange={(e) => setUsername(e.target.value)}
        />
        <fieldset className="portal-checklist-wrap">
          <legend>{t(S.portals)}</legend>
          <p className="muted portal-checklist-help">{t(S.portalsHelp)}</p>
          {touched && !portalsOk && <InlineAlert tone="bad">{t(S.portalsRequired)}</InlineAlert>}
          <PortalChecklist
            chosen={portals}
            onToggle={(role, on) =>
              setPortals((prev) => (on ? [...prev, role] : prev.filter((r) => r !== role)))
            }
          />
        </fieldset>
      </div>
    </Modal>
  );
}

/** Change which portals an existing account holds. */
export function EditPortalsDialog({
  open,
  user,
  onClose,
  onSaved,
}: {
  open: boolean;
  user: IdentityUser | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const [portals, setPortals] = useState<string[]>([]);
  const [seeded, setSeeded] = useState<string | null>(null);

  // Seeded once per opening rather than from an effect, which would overwrite the administrator's ticks on
  // every re-render of the parent list.
  const seedKey = open && user ? user.id : null;
  if (seedKey !== seeded) {
    setSeeded(seedKey);
    // The account holds ISSUER role names; the checklist speaks portal keys. Mapped back through the same
    // table `issuerRoleFor` uses, so a round trip cannot silently drop `lab_tech`.
    setPortals(PORTALS.filter((p) => (user?.roles ?? []).includes(issuerRoleFor(p.role))).map((p) => p.role));
    write.reset();
  }

  async function submit() {
    if (!user) return;
    const ok = await write.run(() => api.setIdentityUserRoles(user.id, portals.map((r) => issuerRoleFor(r as never))));
    if (ok) onSaved();
  }

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={`${t(S.editPortals)}${user ? ` — ${user.displayName}` : ""}`}
      description={t(S.editPortalsHelp)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>
            {t(S.cancel)}
          </Button>
          <Button variant="primary" leadingIcon={<Icon name="check2" />} loading={write.busy} onClick={() => void submit()}>
            {t(S.save)}
          </Button>
        </>
      }
    >
      <div className="stack-3">
        {write.error && <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert>}
        <PortalChecklist
          chosen={portals}
          onToggle={(role, on) => setPortals((prev) => (on ? [...prev, role] : prev.filter((r) => r !== role)))}
        />
      </div>
    </Modal>
  );
}

type ConfirmKind = "reset" | "deactivate" | "reactivate";

/**
 * The three row actions, behind a confirmation each.
 *
 * Every one of them changes somebody else's ability to work, and two of them are felt immediately by a
 * person who is not in the room. A confirmation that states the CONSEQUENCE — signed out of every device,
 * current password keeps working until the link is used — is the difference between an administrator
 * choosing and an administrator finding out.
 */
export function UserActionDialog({
  kind,
  user,
  onClose,
  onDone,
}: {
  kind: ConfirmKind | null;
  user: IdentityUser | null;
  onClose: () => void;
  onDone: (message: Localized) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();

  if (!kind || !user) return null;

  const copy = {
    reset: { title: S.sendResetTitle, body: S.sendResetBody },
    deactivate: { title: S.deactivateTitle, body: S.deactivateBody },
    reactivate: { title: S.reactivateTitle, body: S.reactivateBody },
  }[kind];

  // Stated before the attempt rather than reported after it: the server answers 422 for this, and an
  // administrator who presses a button and reads "no-email-address" has learned it the expensive way.
  const blocked = kind === "reset" && !user.email;

  async function run() {
    if (!user) return;
    const ok = await write.run(async () => {
      if (kind === "reset") await api.sendPasswordResetLink(user.id);
      else if (kind === "deactivate") await api.deactivateIdentityUser(user.id);
      else await api.reactivateIdentityUser(user.id);
    });
    if (ok) {
      onDone(kind === "reset" ? S.resetSent : kind === "deactivate" ? S.deactivate : S.reactivate);
      onClose();
    }
  }

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={t(copy.title)}
      footer={
        <>
          {/* `secondary`, not `ghost`: the safe option must not read as lighter than the destructive one.
              Deactivating signs somebody out of every device, and a barely-there Cancel beside a solid
              Deactivate makes the destructive button the path of least resistance. */}
          <Button variant="secondary" onClick={onClose}>
            {t(S.cancel)}
          </Button>
          <Button
            variant={kind === "deactivate" ? "danger" : "primary"}
            loading={write.busy}
            disabled={blocked}
            onClick={() => void run()}
          >
            {t(S.confirm)}
          </Button>
        </>
      }
    >
      <div className="stack-3">
        {write.error && <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert>}
        {blocked ? <InlineAlert tone="warn">{t(S.sendResetNoEmail)}</InlineAlert> : <p style={{ margin: 0 }}>{t(copy.body)}</p>}
      </div>
    </Modal>
  );
}

/** The row's action buttons — the same three, in the same order, on every row. */
export function UserRowActions({
  user,
  onAct,
  onEditPortals,
}: {
  user: IdentityUser;
  onAct: (kind: ConfirmKind, user: IdentityUser) => void;
  onEditPortals: (user: IdentityUser) => void;
}) {
  const t = useLoc();
  return (
    <span className="chip-row">
      <Button variant="ghost" size="sm" onClick={() => onEditPortals(user)}>
        {t(S.editPortals)}
      </Button>
      <Button variant="ghost" size="sm" onClick={() => onAct("reset", user)}>
        {t(S.sendReset)}
      </Button>
      {user.isActive ? (
        <Button variant="ghost" size="sm" onClick={() => onAct("deactivate", user)}>
          {t(S.deactivate)}
        </Button>
      ) : (
        <Button variant="ghost" size="sm" onClick={() => onAct("reactivate", user)}>
          {t(S.reactivate)}
        </Button>
      )}
    </span>
  );
}

/** Active / de-provisioned, stated in words as well as tone. */
export function AccountStatusChip({ user }: { user: IdentityUser }) {
  const t = useLoc();
  return user.isActive ? (
    <StatusChip kind="ok" label={t({ en: "Active", ar: "نشط" })} />
  ) : (
    <StatusChip kind="neu" label={t({ en: "De-provisioned", ar: "مُعطّل" })} />
  );
}

export const USER_ADMIN_STRINGS = S;
