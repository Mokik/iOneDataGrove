import type { Metadata } from "next";
import { SearchClient } from "./SearchClient";

export const metadata: Metadata = {
  title: "Ricerca trasversale · iOne Data Grove",
  description: "Ricerca unificata nei repository, nelle attività GitHub e nel codice sorgente importato.",
};

export default function SearchPage() {
  return <SearchClient />;
}