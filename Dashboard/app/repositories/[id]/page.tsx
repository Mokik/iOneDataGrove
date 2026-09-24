import type { Metadata } from "next";
import { RepositoryExplorerClient } from "./RepositoryExplorerClient";

export const metadata: Metadata = {
  title: "Repository · iOne Data Grove",
  description: "Esplora i dati GitHub importati in PostgreSQL.",
};

export default async function RepositoryPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  return <RepositoryExplorerClient repositoryId={Number(id)} />;
}
