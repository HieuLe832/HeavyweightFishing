import type { Metadata } from "next";
import { Be_Vietnam_Pro, Bricolage_Grotesque } from "next/font/google";
import { AuthProvider } from "@/lib/auth";
import "./globals.css";

// Both fonts include the Vietnamese subset, so names like "Nguyễn" render in the theme font.
const display = Bricolage_Grotesque({ variable: "--font-display", subsets: ["latin", "vietnamese"] });
const body = Be_Vietnam_Pro({ variable: "--font-body", subsets: ["latin", "vietnamese"], weight: ["400", "500", "600", "700", "800"] });

export const metadata: Metadata = {
  title: "Câu Cá Vạn Cân Chat",
  description: "Chat with your friends and contacts.",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="en" className={`${display.variable} ${body.variable} h-full antialiased`}>
      <body className="min-h-full flex flex-col">
        <AuthProvider>{children}</AuthProvider>
      </body>
    </html>
  );
}
