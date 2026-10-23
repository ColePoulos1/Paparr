import type { Edition } from "./api";

export function hasEditionDetails(edition: Edition | null): boolean {
  return Boolean(edition && (edition.isbnStatus !== "NotProvided" || edition.isbn13 ||
    edition.publisher || edition.publicationYear || edition.language));
}
