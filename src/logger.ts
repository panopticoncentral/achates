import pino from 'pino';

const transport = process.env.NODE_ENV !== 'production'
  ? { target: 'pino-pretty', options: { colorize: true } }
  : undefined;

const logger = pino({
  level: process.env.LOG_LEVEL || 'info',
  transport,
});

export default logger;
