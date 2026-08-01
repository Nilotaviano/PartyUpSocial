import type { QueryClient } from '@tanstack/react-query'
import createClient from 'openapi-fetch'
import type { components, paths } from './schema'

type ErrorMap = Record<string, readonly string[]>

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly errors: ErrorMap

  constructor(status: number, code: string, errors: ErrorMap = {}) {
    super(code)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.errors = errors
  }
}

type ProblemBody = {
  code?: unknown
  errors?: unknown
}

let antiforgeryToken: string | undefined
let antiforgeryRequest: Promise<string> | undefined
let sessionQueryClient: QueryClient | undefined

export const client = createClient<paths>({ baseUrl: '/' })

export function registerSessionQueryClient(queryClient: QueryClient) {
  sessionQueryClient = queryClient
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError
}

export async function refreshAntiforgeryToken(): Promise<string> {
  if (antiforgeryRequest) {
    return antiforgeryRequest
  }

  antiforgeryRequest = client
    .GET('/api/v1/auth/antiforgery')
    .then(({ data }) => {
      if (!data?.token) {
        throw new ApiError(500, 'antiforgery_token_unavailable')
      }

      antiforgeryToken = data.token
      return data.token
    })
    .finally(() => {
      antiforgeryRequest = undefined
    })

  return antiforgeryRequest
}

export function getAntiforgeryToken() {
  return antiforgeryToken
}

function isUnsafeMethod(method: string) {
  return !['GET', 'HEAD', 'OPTIONS', 'TRACE'].includes(method.toUpperCase())
}

function toErrors(value: unknown): ErrorMap {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    return {}
  }

  return Object.fromEntries(
    Object.entries(value).flatMap(([key, messages]) => {
      if (!Array.isArray(messages) || !messages.every((message) => typeof message === 'string')) {
        return []
      }

      return [[key, messages] as const]
    }),
  )
}

async function toApiError(response: Response) {
  let problem: ProblemBody = {}

  try {
    problem = (await response.clone().json()) as ProblemBody
  } catch {
    // Non-problem responses still become a typed error for callers.
  }

  return new ApiError(
    response.status,
    typeof problem.code === 'string' ? problem.code : 'request_failed',
    toErrors(problem.errors),
  )
}

client.use({
  async onRequest({ request }) {
    if (!isUnsafeMethod(request.method)) {
      return undefined
    }

    const token = antiforgeryToken ?? (await refreshAntiforgeryToken())
    const headers = new Headers(request.headers)
    headers.set('X-CSRF-TOKEN', token)
    return new Request(request, { headers })
  },
  async onResponse({ response, schemaPath }) {
    if (response.ok) {
      return undefined
    }

    const error = await toApiError(response)

    if (response.status === 401) {
      await sessionQueryClient?.invalidateQueries({ queryKey: ['session'] })
    }

    if (response.status === 400 && error.code === 'antiforgery_failed' && schemaPath !== '/api/v1/auth/antiforgery') {
      await refreshAntiforgeryToken()
    }

    throw error
  },
})

export type Session = components['schemas']['SessionResponse']
