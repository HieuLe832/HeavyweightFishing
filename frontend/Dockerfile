FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# NEXT_PUBLIC_* values are compiled into the browser bundle, so the API address is a build argument, not a runtime setting.
ARG NEXT_PUBLIC_API_URL=http://localhost:5036
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL
# public/ is empty and git keeps no empty folders, but the runtime stage copies it.
RUN mkdir -p public && npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=3000 HOSTNAME=0.0.0.0
COPY --from=build /app/.next/standalone ./
COPY --from=build /app/.next/static ./.next/static
COPY --from=build /app/public ./public
EXPOSE 3000
CMD ["node", "server.js"]
