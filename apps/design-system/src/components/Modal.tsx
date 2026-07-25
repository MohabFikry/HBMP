import * as Dialog from "@radix-ui/react-dialog";
import type { ReactNode } from "react";

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
}

/**
 * Modal / sheet — Radix Dialog gives focus trap, return-focus, Esc-to-close, scrim, and labelled-by title.
 * Level-3 glass over a scrim; body content sits on an inner opaque block (glass contrast contract, 0B §4).
 */
export function Modal({ open, onOpenChange, title, description, children, footer, trigger }: ModalProps) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      {trigger && <Dialog.Trigger asChild>{trigger}</Dialog.Trigger>}
      <Dialog.Portal>
        <Dialog.Overlay className="mrs-overlay">
          <Dialog.Content className="mrs-modal">
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
