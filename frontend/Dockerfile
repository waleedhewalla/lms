FROM node:20-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . ./
ENV NEXT_TELEMETRY_DISABLED=1
RUN npm run build

FROM node:20-alpine
WORKDIR /app
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1
COPY --from=build /app/package*.json ./
COPY --from=build /app/.next ./.next
COPY --from=build /app/next.config.ts ./next.config.ts
RUN npm ci --omit=dev && adduser -D nextjs && chown -R nextjs /app
USER nextjs
EXPOSE 3000
CMD ["npm", "start"]
