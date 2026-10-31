import type { Metadata } from "next";
import { Baloo_2, Nunito } from "next/font/google";
import "./globals.css";

// Both fonts include the Vietnamese subset, so names like "Nguyễn" render in the theme font.
const display = Baloo_2({ variable: "--font-display", subsets: ["latin", "vietnamese"] });
const body = Nunito({ variable: "--font-body", subsets: ["latin", "vietnamese"] });

export const metadata: Metadata = {
  title: "Câu Cá Vạn Cân Chat",
  description: "Chat with your friends and contacts.",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="en" className={`${display.variable} ${body.variable} h-full antialiased`}>
      <body className="min-h-full flex flex-col">{children}</body>
    </html>
  );
}
