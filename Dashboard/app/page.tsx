import type { Metadata } from "next";
import { DashboardClient } from "./DashboardClient";

export const metadata: Metadata = {
  title: "iOne Data Grove — Dashboard",
  description: "Monitoraggio locale delle importazioni dati di iOne Data Grove.",
};

export default function Home() {
  return <DashboardClient />;
}
