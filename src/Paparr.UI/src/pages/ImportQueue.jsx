import { useState, useEffect } from 'react'
import ImportJobCard from '../components/ImportJobCard'
import './ImportQueue.css'

function ImportQueue({ apiClient }) {
  const [jobs, setJobs] = useState([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(null)
  const [filter, setFilter] = useState('AwaitingApproval')

  useEffect(() => {
    fetchJobs()
    // Auto-refresh every 10 seconds
    const interval = setInterval(fetchJobs, 10000)
    return () => clearInterval(interval)
  }, [])

  const fetchJobs = async () => {
    try {
      setLoading(true)
      const response = await apiClient.get('/imports')
      setJobs(response.data)
      setError(null)
    } catch (err) {
      setError(err.message)
    } finally {
      setLoading(false)
    }
  }

  const handleAcceptCandidate = async (jobId, candidateId) => {
    try {
      await apiClient.post(`/imports/${jobId}/accept/${candidateId}`)
      await fetchJobs()
    } catch (err) {
      alert(`Error accepting candidate: ${err.message}`)
    }
  }

  const handleRetry = async (jobId) => {
    try {
      await apiClient.post(`/imports/${jobId}/retry`)
      await fetchJobs()
    } catch (err) {
      alert(`Error retrying import: ${err.message}`)
    }
  }

  const filteredJobs = jobs.filter(job => {
    if (filter === 'all') return true
    return job.status === filter
  })

  return (
    <div className="import-queue">
      <div className="queue-header">
        <h2>Import Queue</h2>
        <button className="refresh-btn" onClick={fetchJobs} disabled={loading}>
          {loading ? 'Refreshing...' : 'Refresh'}
        </button>
      </div>

      <div className="filter-buttons">
        <button
          className={`filter-btn ${filter === 'AwaitingApproval' ? 'active' : ''}`}
          onClick={() => setFilter('AwaitingApproval')}
        >
          Awaiting Approval
        </button>
        <button
          className={`filter-btn ${filter === 'Processing' ? 'active' : ''}`}
          onClick={() => setFilter('Processing')}
        >
          Processing
        </button>
        <button
          className={`filter-btn ${filter === 'Failed' ? 'active' : ''}`}
          onClick={() => setFilter('Failed')}
        >
          Failed
        </button>
        <button
          className={`filter-btn ${filter === 'all' ? 'active' : ''}`}
          onClick={() => setFilter('all')}
        >
          All
        </button>
      </div>

      {error && <div className="error-message">{error}</div>}

      {filteredJobs.length === 0 ? (
        <div className="empty-state">
          <p>No {filter !== 'all' ? filter.toLowerCase() : ''} imports</p>
        </div>
      ) : (
        <div className="jobs-grid">
          {filteredJobs.map(job => (
            <ImportJobCard
              key={job.id}
              job={job}
              onAcceptCandidate={handleAcceptCandidate}
              onRetry={handleRetry}
            />
          ))}
        </div>
      )}
    </div>
  )
}

export default ImportQueue
