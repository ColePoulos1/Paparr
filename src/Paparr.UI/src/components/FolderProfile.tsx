import { Heading } from "@astryxdesign/core/Heading";
import { MetadataList, MetadataListItem } from "@astryxdesign/core/MetadataList";
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
      <Heading level={3}>Folder profile</Heading>
      <MetadataList columns="single">
        <MetadataListItem label="Profile">{profile.name}</MetadataListItem>
        <MetadataListItem label="Watch subfolder">{profile.sourceFolder}</MetadataListItem>
        <MetadataListItem label="Library collection">{profile.collection ?? "Default library layout"}</MetadataListItem>
      </MetadataList>
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
