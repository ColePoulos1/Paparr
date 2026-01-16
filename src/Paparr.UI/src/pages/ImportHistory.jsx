import { useState, useEffect } from 'react'
import './ImportHistory.css'

function ImportHistory({ apiClient }) {
  const [jobs, setJobs] = useState([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState(null)

  useEffect(() => {
    fetchJobs()
    const interval = setInterval(fetchJobs, 30000)
    return () => clearInterval(interval)
  }, [])

  const fetchJobs = async () => {
    try {
      setLoading(true)
      const response = await apiClient.get('/imports')
      // Filter completed jobs
      const completed = response.data.filter(j => j.status === 'Completed')
      setJobs(completed.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt)))
      setError(null)
    } catch (err) {
      setError(err.message)
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="import-history">
      <div className="history-header">
        <h2>Import History</h2>
        <button className="refresh-btn" onClick={fetchJobs} disabled={loading}>
          {loading ? 'Refreshing...' : 'Refresh'}
        </button>
      </div>

      {error && <div className="error-message">{error}</div>}

      {jobs.length === 0 ? (
        <div className="empty-state">
          <p>No completed imports</p>
        </div>
      ) : (
        <div className="history-table">
          <table>
            <thead>
              <tr>
                <th>Title</th>
                <th>Author</th>
                <th>Source</th>
                <th>Imported At</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map(job => (
                <tr key={job.id}>
                  <td className="title-cell">{job.acceptedBook?.title || 'N/A'}</td>
                  <td className="author-cell">{job.acceptedBook?.author || 'N/A'}</td>
                  <td className="source-cell">{job.acceptedBook?.source || 'N/A'}</td>
                  <td className="date-cell">
                    {new Date(job.acceptedBook?.importedAt).toLocaleDateString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

export default ImportHistory
