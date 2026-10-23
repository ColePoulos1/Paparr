import type { Edition } from "../api";
import { hasEditionDetails } from "../edition";
import "./EditionDetails.css";

const isbnLabels: Record<string, string> = {
  NotProvided: "Not supplied",
  Invalid: "Invalid ISBN supplied",
  Ambiguous: "Multiple ISBNs reported",
};

export default function EditionDetails({ edition }: { edition: Edition | null }) {
  if (!edition || !hasEditionDetails(edition)) return <span className="edition-empty">No edition details supplied.</span>;
  return (
    <span className="edition-details" role="group" aria-label="Edition details">
      <span><span className="edition-label">ISBN-13</span>{edition.isbn13 ?? isbnLabels[edition.isbnStatus] ?? "Not supplied"}</span>
      <span><span className="edition-label">Publisher</span>{edition.publisher ?? "Not supplied"}</span>
      <span><span className="edition-label">Year</span>{edition.publicationYear ?? "Not supplied"}</span>
      <span><span className="edition-label">Language</span>{edition.language ?? "Not supplied"}</span>
    </span>
  );
}
