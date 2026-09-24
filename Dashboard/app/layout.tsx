import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "iOne Data Grove",
  description: "Dashboard locale per il monitoraggio delle importazioni dati.",
  icons: { icon: "/favicon.svg", shortcut: "/favicon.svg" },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="it">
      <body>{children}</body>
    </html>
  );
}
