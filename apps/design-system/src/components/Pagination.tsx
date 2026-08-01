import { useId } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";
import { useTheme } from "../theme/ThemeProvider";

export interface PaginationProps {
  /** 1-based. */
  page: number;
  pageSize: number;
  /** Rows after search and filters, across every page — NOT the length of the current page. */
  total: number;
  onPageChange: (page: number) => void;
  /** Omit to render a fixed page size with no picker. */
  onPageSizeChange?: (pageSize: number) => void;
  pageSizeOptions?: readonly number[];
  className?: string;
}

const DEFAULT_PAGE_SIZES = [10, 25, 50, 100] as const;

/**
 * The pager under a worklist: where you are, how much there is, and one step in either direction.
 *
 * ============================================================================================================
 * WHY IT STATES A RANGE AND A TOTAL, NOT A PAGE NUMBER
 * ============================================================================================================
 * "Page 2 of 9" tells an operator nothing they can act on. "26–50 of 210" tells them how much work is left,
 * which is the question a queue is actually managed against — and it is the number that makes a filter's
 * effect visible: applying "Info requested" and watching 210 become 12 is the feedback that the filter worked.
 *
 * ============================================================================================================
 * WHY THERE ARE NO NUMBERED PAGE LINKS
 * ============================================================================================================
 * A numbered pager is for a corpus you navigate by position — page 7 of a search result. These are queues
 * worked front to back, where position is an artifact of the sort and jumping to page 7 means nothing. Two
 * buttons and a size picker cover every real gesture, and they leave room for the range to be legible.
 *
 * First/last are deliberately absent for the same reason: "the end of the queue" is not a destination anyone
 * needs, and each additional control makes the two that matter harder to hit.
 */
export function Pagination({
  page,
  pageSize,
  total,
  onPageChange,
  onPageSizeChange,
  pageSizeOptions = DEFAULT_PAGE_SIZES,
  className,
}: PaginationProps) {
  const { lang } = useTheme();
  const ar = lang === "ar";
  const sizeId = useId();

  // Clamped, because `total` changes under the pager: filtering a 9-page queue down to 1 page while sitting on
  // page 4 must not render "Showing 76–100 of 12" beside an empty table.
  const pageCount = Math.max(1, Math.ceil(total / pageSize));
  const current = Math.min(Math.max(page, 1), pageCount);
  const first = total === 0 ? 0 : (current - 1) * pageSize + 1;
  const last = Math.min(current * pageSize, total);

  const range = ar
    ? `عرض ${first}–${last} من ${total}`
    : `Showing ${first}–${last} of ${total}`;
  const prevLabel = ar ? "السابق" : "Previous";
  const nextLabel = ar ? "التالي" : "Next";
  const perPage = ar ? "لكل صفحة" : "Per page";

  return (
    // `nav`, so a screen-reader user can jump to the pager rather than tabbing through the whole table to
    // reach it. Named, because a page may hold more than one.
    <nav className={cx("mrs-pager", className)} aria-label={ar ? "ترقيم الصفحات" : "Pagination"}>
      {/*
        The range is the live region, not the table. Announcing the table would re-read every row after each
        step; announcing the range says what changed in one phrase — which is the whole content of the event.
      */}
      <p className="mrs-pager-range tnum" aria-live="polite">{range}</p>

      <div className="mrs-pager-controls">
        {onPageSizeChange && (
          <div className="mrs-pager-size">
            <label className="mrs-label" htmlFor={sizeId}>{perPage}</label>
            <select
              id={sizeId}
              className="mrs-control"
              value={pageSize}
              onChange={(e) => {
                // Back to the first page: keeping the page number while the size changes moves the operator
                // to a different part of the queue than the one they were reading.
                onPageSizeChange(Number(e.currentTarget.value));
                onPageChange(1);
              }}
            >
              {pageSizeOptions.map((n) => (
                <option key={n} value={n}>{n}</option>
              ))}
            </select>
          </div>
        )}

        {/* Disabled rather than hidden at the ends: a control that disappears takes the layout with it, and
            the operator loses the target they were aiming at. */}
        <button
          type="button"
          className="mrs-pager-step"
          onClick={() => onPageChange(current - 1)}
          disabled={current <= 1}
        >
          {/* The chevron points along the direction of travel, which in Arabic is the other way. The icon is
              rotated by CSS off `[dir]`, so it follows the document rather than a prop nobody remembers. */}
          <Icon name="chevron" width={14} height={14} aria-hidden="true" />
          <span>{prevLabel}</span>
        </button>
        <button
          type="button"
          className="mrs-pager-step"
          onClick={() => onPageChange(current + 1)}
          disabled={current >= pageCount}
        >
          <span>{nextLabel}</span>
          <Icon name="chevron" width={14} height={14} aria-hidden="true" />
        </button>
      </div>
    </nav>
  );
}
