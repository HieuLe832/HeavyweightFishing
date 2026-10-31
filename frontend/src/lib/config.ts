export const config = {
  /** Where the Chat API listens; see .env.example. */
  apiUrl: (process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5036").replace(/\/+$/, ""),
};
