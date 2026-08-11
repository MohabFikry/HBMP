import { useState } from "react";
import { Button, Icon, InlineAlert, InputField, Modal, StatusChip } from "@mersal/design-system";
import type { IdentityUser, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useWrite } from "../api/useWrite";
import { issuerRoleFor } from "../config";
import { PORTALS, ZONES } from "../portals/catalog";
import { PhotoPicker, PHOTO_PICKER_STRINGS } from "../shell/PhotoPicker";
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
  position: { en: "Position", ar: "المسمى الوظيفي" },
  positionHelp: {
    en: "Their job title, e.g. Senior Pharmacist. It appears beside their name across the platform and grants nothing — access comes from the portals below.",
    ar: "المسمى الوظيفي، مثل صيدلي أول. يظهر بجوار الاسم في كل أنحاء المنصة ولا يمنح أي صلاحية — الوصول يأتي من البوابات أدناه.",
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

  /*
    28.10 — ONE "Edit", not two.

    There were two row buttons and neither could correct a name or an address: "Change portals" existed, and
    `api.updateIdentityUser` — the endpoint that fixes a mistyped email — had no caller anywhere in the app.
    So the recorded remedy for a typo in the address somebody signs in with was to abandon the account and
    make another one.

    Merging them is not just about adding the missing half. An administrator opening a person's record is
    fixing THAT PERSON, and "their details" and "what they can reach" are two parts of one answer; splitting
    them across two buttons made the reader choose a route before knowing which one held the field they
    wanted.
  */
  edit: { en: "Edit", ar: "تعديل" },
  editTitle: { en: "Edit this account", ar: "تعديل هذا الحساب" },
  editHelp: {
    en: "Changes to portals take effect on their next sign-in, or within five minutes on this one. Changing the email address changes what they sign in with.",
    ar: "تسري تغييرات البوابات عند تسجيل الدخول التالي، أو خلال خمس دقائق على الجلسة الحالية. تغيير البريد الإلكتروني يغيّر ما يسجّلون الدخول به.",
  },
  emailChanged: {
    en: "Saved. They now sign in with the new address.",
    ar: "تم الحفظ. سيسجّلون الدخول الآن بالبريد الجديد.",
  },
  detailsSaved: { en: "Saved.", ar: "تم الحفظ." },
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
  /*
    28.10 — the OUTCOME sentences.

    `UserActionDialog` used to announce `S.deactivate` and `S.reactivate` on success — the BUTTON LABELS. So
    the polite live region, which exists to tell a screen-reader user what just happened, said "Deactivate".
    Not a sentence, not in the past tense, and indistinguishable from the control that had just been pressed:
    the one reading it could not tell whether the account had been deactivated or whether they were being
    offered the chance to.
  */
  deactivated: {
    en: "Account deactivated. They are signed out of every device.",
    ar: "تم تعطيل الحساب. تم إخراجهم من كل الأجهزة.",
  },
  reactivated: {
    en: "Account reactivated. They can sign in again.",
    ar: "تمت إعادة تفعيل الحساب. يمكنهم تسجيل الدخول مجددًا.",
  },
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
  const [position, setPosition] = useState("");
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
        // Optional, and left absent rather than sent as "" when unfilled — the server treats blank as "none
        // recorded", and so does everything that renders it.
        position: position.trim() || undefined,
        tenantId: "",
        // The ISSUER's names, not the portal keys. `lab` would be a 422; `lab_tech` is the grant.
        roles: portals.map((r) => issuerRoleFor(r as never)),
      });
      onCreated({ resetLinkSent: result.resetLinkSent });
    });
    if (ok) {
      setDisplayName("");
      setEmail("");
      setPosition("");
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
          label={t(S.position)}
          help={t(S.positionHelp)}
          autoComplete="off"
          value={position}
          onChange={(e) => setPosition(e.target.value)}
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

/**
 * Edit an existing account: who they are, what they sign in with, and what they can reach.
 *
 * <p>Two writes behind one Save, and the ORDER matters. Details go first: if the roles write then fails, the
 * administrator is looking at a corrected name beside an unchanged portal list and a stated error, which is
 * a legible half-done state. The other order leaves a person's access changed while their record still shows
 * the typo that prompted the edit — and nothing on screen would say which half landed.</p>
 *
 * <p>Neither write is sent when nothing in its half changed. That is not an optimisation: every one of these
 * endpoints writes an audit event, and an "email changed" entry recording no change is noise in the record
 * an access review reads.</p>
 */
export function EditUserDialog({
  open,
  user,
  onClose,
  onSaved,
}: {
  open: boolean;
  user: IdentityUser | null;
  onClose: () => void;
  onSaved: (message: Localized) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const [displayName, setDisplayName] = useState("");
  const [email, setEmail] = useState("");
  const [position, setPosition] = useState("");
  const [portals, setPortals] = useState<string[]>([]);
  const [touched, setTouched] = useState(false);
  const [seeded, setSeeded] = useState<string | null>(null);

  // Seeded once per opening rather than from an effect, which would overwrite the administrator's ticks on
  // every re-render of the parent list.
  const seedKey = open && user ? user.id : null;
  if (seedKey !== seeded) {
    setSeeded(seedKey);
    setDisplayName(user?.displayName ?? "");
    setEmail(user?.email ?? "");
    setPosition(user?.position ?? "");
    // The account holds ISSUER role names; the checklist speaks portal keys. Mapped back through the same
    // table `issuerRoleFor` uses, so a round trip cannot silently drop `lab_tech`.
    setPortals(PORTALS.filter((p) => (user?.roles ?? []).includes(issuerRoleFor(p.role))).map((p) => p.role));
    setTouched(false);
    write.reset();
  }

  const nameOk = displayName.trim().length > 0;
  // An account may legitimately have NO address (service accounts, fixtures predating 28.8), so blank is
  // allowed here where it is required at creation. What is not allowed is a malformed one.
  const emailOk = email.trim().length === 0 || looksLikeEmail(email);
  const portalsOk = portals.length > 0;

  const nameChanged = user ? displayName.trim() !== user.displayName : false;
  const emailChanged = user ? email.trim() !== (user.email ?? "") : false;
  const positionChanged = user ? position.trim() !== (user.position ?? "") : false;
  const rolesChanged = user
    ? [...portals].sort().join() !== PORTALS.filter((p) => user.roles.includes(issuerRoleFor(p.role))).map((p) => p.role).sort().join()
    : false;

  async function submit() {
    setTouched(true);
    if (!user || !nameOk || !emailOk || !portalsOk) return;
    const ok = await write.run(async () => {
      if (nameChanged || emailChanged || positionChanged) {
        await api.updateIdentityUser(user.id, {
          displayName: nameChanged ? displayName.trim() : undefined,
          email: emailChanged ? email.trim() : undefined,
          // Sent as "" when the administrator has emptied the box, which is how the field is CLEARED.
          // `undefined` would leave a title that no longer applies in place with no way to remove it.
          position: positionChanged ? position.trim() : undefined,
        });
      }
      if (rolesChanged) await api.setIdentityUserRoles(user.id, portals.map((r) => issuerRoleFor(r as never)));
    });
    // The address is called out on its own because it is the one change that alters how the person GETS IN.
    // Being told only "Saved" after changing it leaves them to discover the consequence at the login screen.
    if (ok) onSaved(emailChanged ? S.emailChanged : S.detailsSaved);
  }

  return (
    <Modal
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={`${t(S.editTitle)}${user ? ` — ${user.displayName}` : ""}`}
      description={t(S.editHelp)}
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
        {write.error && (
          <InlineAlert tone="bad">
            {/* A 409 on this endpoint has exactly one cause — the address already belongs to somebody else.
                "Conflict" would leave the administrator guessing which of three fields to change. */}
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
          label={t(S.position)}
          help={t(S.positionHelp)}
          autoComplete="off"
          value={position}
          onChange={(e) => setPosition(e.target.value)}
        />
        {/*
          28.15 — an administrator sets somebody's photograph. Ordinary: a headshot arrives at HR from a
          person who does not administer their own account.

          It writes IMMEDIATELY, not on Save, and that is deliberate. The photo is a separate resource with
          its own endpoint; folding it into this dialog's Save would mean a failed photo upload rolling back
          a name change that succeeded, or the reverse. The picker reports its own outcome, and the avatar
          beside it is the confirmation.
        */}
        <fieldset className="portal-checklist-wrap">
          <legend>{t(PHOTO_PICKER_STRINGS.photo)}</legend>
          {/* `buttons`, not the pane's hover overlay: every other row in this dialog is a labelled control,
              and a picture that only reveals its verb on hover would be the one thing here that hides it. */}
          {user && (
            <PhotoPicker userId={user.id} name={user.displayName} adminForUserId={user.id} variant="buttons" t={t} />
          )}
        </fieldset>
        <fieldset className="portal-checklist-wrap">
          <legend>{t(S.portals)}</legend>
          <p className="muted portal-checklist-help">{t(S.portalsHelp)}</p>
          {touched && !portalsOk && <InlineAlert tone="bad">{t(S.portalsRequired)}</InlineAlert>}
          <PortalChecklist
            chosen={portals}
            onToggle={(role, on) => setPortals((prev) => (on ? [...prev, role] : prev.filter((r) => r !== role)))}
          />
        </fieldset>
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
      onDone(kind === "reset" ? S.resetSent : kind === "deactivate" ? S.deactivated : S.reactivated);
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

/**
 * The row's action buttons — the same three, in the same order, on every row.
 *
 * <p>Three, not four: 28.10 merged "Change portals" into "Edit". And the destructive one is `danger` rather
 * than a fourth identical ghost. Four buttons of identical weight give the eye nothing to sort them by, so
 * the one that signs a colleague out of every device sat with exactly the visual authority of the one that
 * opens a form. Colour is not the only cue — the confirmation states the consequence, and the label says the
 * verb — but it is the one available before the click.</p>
 *
 * <p>Reactivate stays `ghost`: restoring somebody's access is the safe direction, and painting it red to
 * match its opposite would teach the colour to mean "account lifecycle" instead of "this is destructive".</p>
 */
export function UserRowActions({
  user,
  onAct,
  onEdit,
}: {
  user: IdentityUser;
  onAct: (kind: ConfirmKind, user: IdentityUser) => void;
  onEdit: (user: IdentityUser) => void;
}) {
  const t = useLoc();
  return (
    <span className="chip-row">
      <Button variant="ghost" size="sm" leadingIcon={<Icon name="pen" />} onClick={() => onEdit(user)}>
        {t(S.edit)}
      </Button>
      <Button variant="ghost" size="sm" onClick={() => onAct("reset", user)}>
        {t(S.sendReset)}
      </Button>
      {user.isActive ? (
        <Button variant="danger" size="sm" onClick={() => onAct("deactivate", user)}>
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
