import * as Dialog from "@radix-ui/react-dialog";
import type { ReactNode } from "react";
import { Icon } from "./Icon";

export interface ModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  /** Optional short description tied via aria-describedby. */
  description?: string;
  children: ReactNode;
  footer?: ReactNode;
  /** Trigger element (optional — modal can also be controlled). */
  trigger?: ReactNode;
  /**
   * Accessible name for the close control. Defaults to English; pass the active locale's word on a screen
   * that has one, since this is the only text the component renders on its own behalf.
   */
  closeLabel?: string;
  /** Widen for reference content read by scanning columns (contracts, code lists). Confirmations stay narrow. */
  wide?: boolean;
}

/**
 * Modal / sheet — Radix Dialog gives focus trap, return-focus, Esc-to-close, scrim, and labelled-by title.
 * Level-3 glass over a scrim; body content sits on an inner opaque block (glass contrast contract, 0B §4).
 */
export function Modal({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer,
  trigger,
  closeLabel = "Close",
  wide = false,
}: ModalProps) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      {trigger && <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>}
      <Dialog.Portal>
        <Dialog.Overlay className="mrs-overlay">
          <Dialog.Content className="mrs-modal" data-wide={wide || undefined}>
            {/*
              Esc and an outside click already closed this — Radix gives both — but neither is VISIBLE, so a
              reference modal (the bulk column contract, a confirmation) offered a mouse or touch user no way
              out that they could see. "It is dismissible" and "it looks dismissible" are different claims,
              and only the second one is on screen. A close control is not a footer button: it belongs to the
              dialog chrome, so it renders here for every modal rather than being remembered per call site.
            */}
            <Dialog.Close className="mrs-modal-close" aria-label={closeLabel}>
              <Icon name="cross" />
            </Dialog.Close>
            <Dialog.Title style={{ fontSize: "var(--fs-title-3)" }}>{title}</Dialog.Title>
            {description && <Dialog.Description className="muted">{description}</Dialog.Description>}
            <div className="mrs-modal-body">{children}</div>
            {footer && (
              <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: "var(--sp4)" }}>
                {footer}
              </div>
            )}
          </Dialog.Content>
        </Dialog.Overlay>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
