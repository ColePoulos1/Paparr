import type { ImportProfileDto } from "../generated/types.gen";
import "./FolderProfile.css";

export default function FolderProfile({
  profile,
}: {
  profile?: ImportProfileDto;
}) {
  if (!profile?.name) return null;
  return (
    <section className="folder-profile" aria-label="Folder profile">
      <span className="tiny-label">FOLDER PROFILE</span>
      <strong>{profile.name}</strong>
      <dl>
        <div>
          <dt>Watch subfolder</dt>
          <dd>{profile.sourceFolder}</dd>
        </div>
        <div>
          <dt>Library collection</dt>
          <dd>{profile.collection ?? "Default library layout"}</dd>
        </div>
      </dl>
      <p>
        Filename fallback:{" "}
        {profile.filenameOrder === "AuthorTitle"
          ? "Author — Title"
          : "Title — Author"}
        . Embedded metadata is preferred.
      </p>
      <p>
        This import keeps the profile selected when it was first discovered.
      </p>
    </section>
  );
}
