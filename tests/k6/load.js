import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '5s', target: 5 },  // Ramp up to 5 VUs in 5 seconds
    { duration: '10s', target: 5 }, // Stay at 5 VUs for 10 seconds
    { duration: '5s', target: 0 },  // Ramp down to 0 VUs in 5 seconds
  ],
  thresholds: {
    http_req_duration: ['p(95)<500'], // 95% of requests should be below 500ms
    http_req_failed: ['rate<0.01'],   // Error rate should be less than 1%
  },
};

export default function () {
  // Test the frontend Next.js server locally
  const resWeb = http.get('http://host.docker.internal:8000');
  check(resWeb, {
    'web status is 200': (r) => r.status === 200,
  });

  // Test the backend API health endpoint
  const resApi = http.get('http://host.docker.internal:5299/health');
  check(resApi, {
    'api status is 200': (r) => r.status === 200,
  });

  sleep(1);
}
